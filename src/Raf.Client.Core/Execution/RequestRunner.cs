using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>El request no se puede armar. El mensaje no incluye valores de variables.</summary>
public sealed class RequestBuildException(string message) : Exception(message);

/// <summary>Ejecuta requests desde el cliente: merge del sample, resolución de variables y envío.</summary>
public sealed class RequestRunner : IDisposable
{
    /// <summary>Tope de lo que se conserva en memoria y se muestra de un body.</summary>
    public const int PreviewLimit = 2 * 1024 * 1024;

    private readonly HttpClient _strict = CreateClient(acceptInvalidCertificates: false);
    private readonly HttpClient _permissive = CreateClient(acceptInvalidCertificates: true);

    private static HttpClient CreateClient(bool acceptInvalidCertificates)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false, // cada request envía solo las cookies que declara
        };
        if (acceptInvalidCertificates)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static RequestSpec Effective(RequestSpec request, SampleOverrides? sample) =>
        ActiveParts(sample is null ? request : SampleMerger.Merge(request, sample));

    /// <summary>Informa las variables sin resolver, solo de las partes del request que se van a enviar.</summary>
    public static RunPreflight Check(RequestSpec request, SampleOverrides? sample, VariableContext variables)
    {
        var resolver = variables.CreateResolver();
        resolver.Resolve(Effective(request, sample));
        var unresolved = resolver.Missing.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new RunPreflight(
            unresolved.Where(k => !variables.IsUnreadable(k)).ToList(),
            unresolved.Where(variables.IsUnreadable).ToList());
    }

    /// <summary>URL para el historial, sin valores de variables secretas ni de la autenticación.</summary>
    public static string DescribeUrl(RequestSpec request, SampleOverrides? sample, VariableContext variables)
    {
        var spec = variables.CreatePublicResolver().Resolve(Effective(request, sample));
        return AppendQuery(spec.Url.Trim(), spec.QueryParams);
    }

    public async Task<RunResult> RunAsync(RunInput input, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(input.Options.Timeout);

        try
        {
            var spec = input.Variables.CreateResolver().Resolve(Effective(input.Request, input.Sample));
            using var message = BuildMessage(spec);
            var client = input.Options.AcceptInvalidCertificates ? _permissive : _strict;

            // Se lee por partes para que el timeout cubra también la descarga del body.
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await ReadBodyAsync(response, timeout.Token);
            stopwatch.Stop();

            return new RunResult
            {
                Outcome = RunOutcome.Completed,
                Elapsed = stopwatch.Elapsed,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = response.Headers.Concat(response.Content.Headers)
                    .SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v))).ToList(),
                Body = body,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new RunResult { Outcome = RunOutcome.Cancelled, Elapsed = stopwatch.Elapsed, Error = "Ejecución cancelada." };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new RunResult
            {
                Outcome = RunOutcome.TimedOut,
                Elapsed = stopwatch.Elapsed,
                Error = $"Sin respuesta después de {input.Options.Timeout.TotalSeconds:0.#} s.",
            };
        }
        catch (Exception e) when (e is HttpRequestException or RequestBuildException or IOException)
        {
            return new RunResult
            {
                Outcome = RunOutcome.Failed,
                Elapsed = stopwatch.Elapsed,
                Error = input.Variables.Scrub(Describe(e)),
            };
        }
    }

    private static string Describe(Exception e) =>
        e is HttpRequestException { InnerException: { } inner } ? $"{e.Message} ({inner.Message})" : e.Message;

    /// <summary>Descarta lo que no se envía: filas desactivadas y datos de otros tipos de body o auth.</summary>
    private static RequestSpec ActiveParts(RequestSpec spec) => spec with
    {
        Headers = Enabled(spec.Headers),
        QueryParams = Enabled(spec.QueryParams),
        Body = spec.Body.Type switch
        {
            BodyType.None => BodySpec.None,
            BodyType.FormUrlEncoded => new BodySpec(BodyType.FormUrlEncoded, Form: Enabled(spec.Body.Form ?? [])),
            var t => new BodySpec(t, spec.Body.Content ?? ""),
        },
        Auth = spec.Auth.Type switch
        {
            AuthType.None => AuthSpec.None,
            AuthType.Bearer => new AuthSpec(AuthType.Bearer, Token: spec.Auth.Token),
            AuthType.Basic => new AuthSpec(AuthType.Basic, Username: spec.Auth.Username, Password: spec.Auth.Password),
            _ => new AuthSpec(AuthType.ApiKey, ApiKeyName: spec.Auth.ApiKeyName, ApiKeyValue: spec.Auth.ApiKeyValue, ApiKeyIn: spec.Auth.ApiKeyIn),
        },
    };

    private static List<KeyValueItem> Enabled(IReadOnlyList<KeyValueItem> items) =>
        items.Where(i => i.Enabled && !string.IsNullOrWhiteSpace(i.Key)).ToList();

    private static HttpRequestMessage BuildMessage(RequestSpec spec)
    {
        var query = spec.QueryParams.ToList();
        if (spec.Auth is { Type: AuthType.ApiKey, ApiKeyIn: ApiKeyLocation.Query, ApiKeyName: { Length: > 0 } queryName })
            query.Add(new KeyValueItem(queryName, spec.Auth.ApiKeyValue ?? ""));

        var uri = BuildUri(spec.Url, query);
        if (string.IsNullOrWhiteSpace(spec.Method)) throw new RequestBuildException("Falta el método HTTP.");

        HttpMethod method;
        try
        {
            method = new HttpMethod(spec.Method.Trim().ToUpperInvariant());
        }
        catch (FormatException)
        {
            throw new RequestBuildException("El método HTTP no es válido.");
        }

        var message = new HttpRequestMessage(method, uri) { Content = BuildContent(spec.Body) };
        try
        {
            ApplyHeaders(message, spec.Headers);
            ApplyAuth(message, spec.Auth);
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private static Uri BuildUri(string url, IReadOnlyList<KeyValueItem> query)
    {
        var text = AppendQuery(url.Trim(), query);
        if (text.Length == 0) throw new RequestBuildException("Falta la URL.");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new RequestBuildException("La URL no es válida. Revisá que sea http(s) y que sus variables tengan valor.");
        return uri;
    }

    private static string AppendQuery(string url, IReadOnlyList<KeyValueItem> query)
    {
        if (query.Count == 0) return url;

        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var fragment = hash >= 0 ? url[hash..] : "";
        var head = hash >= 0 ? url[..hash] : url;
        var pairs = string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        var separator = !head.Contains('?', StringComparison.Ordinal) ? "?" : head[^1] is '?' or '&' ? "" : "&";
        return head + separator + pairs + fragment;
    }

    private static HttpContent? BuildContent(BodySpec body) => body.Type switch
    {
        BodyType.None => null,
        BodyType.Json => new StringContent(body.Content ?? "", Encoding.UTF8, "application/json"),
        BodyType.Text => new StringContent(body.Content ?? "", Encoding.UTF8, "text/plain"),
        BodyType.Xml => new StringContent(body.Content ?? "", Encoding.UTF8, "application/xml"),
        BodyType.FormUrlEncoded => new FormUrlEncodedContent(
            (body.Form ?? []).Select(f => KeyValuePair.Create(f.Key, f.Value))),
        _ => throw new RequestBuildException("Tipo de body no soportado."),
    };

    private static void ApplyHeaders(HttpRequestMessage message, IReadOnlyList<KeyValueItem> headers)
    {
        using var contentProbe = new ByteArrayContent([]);
        var replacedContentHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            var key = header.Key.Trim();
            if (message.Headers.TryAddWithoutValidation(key, header.Value)) continue;

            if (!contentProbe.Headers.TryAddWithoutValidation(key, header.Value))
                throw new RequestBuildException($"El header '{key}' no es válido.");

            // Content-Length lo calcula HttpClient; y sin body no hay dónde poner los headers de contenido.
            if (message.Content is null || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (replacedContentHeaders.Add(key)) message.Content.Headers.Remove(key);
            message.Content.Headers.TryAddWithoutValidation(key, header.Value);
        }
    }

    private static void ApplyAuth(HttpRequestMessage message, AuthSpec auth)
    {
        switch (auth.Type)
        {
            case AuthType.Bearer:
                SetAuthorization(message, $"Bearer {auth.Token}");
                break;
            case AuthType.Basic:
                var raw = Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}");
                SetAuthorization(message, $"Basic {Convert.ToBase64String(raw)}");
                break;
            case AuthType.ApiKey when auth is { ApiKeyIn: ApiKeyLocation.Header, ApiKeyName: { Length: > 0 } name }:
                message.Headers.Remove(name);
                if (!message.Headers.TryAddWithoutValidation(name, auth.ApiKeyValue ?? ""))
                    throw new RequestBuildException("El nombre de la API key no es un header válido.");
                break;
        }
    }

    private static void SetAuthorization(HttpRequestMessage message, string value)
    {
        message.Headers.Remove("Authorization");
        message.Headers.TryAddWithoutValidation("Authorization", value);
    }

    private static async Task<ResponseBody> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        FileStream? overflow = null;
        byte[]? preview = null;
        var buffer = new byte[81920];
        long total = 0;

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (overflow is null && memory.Length + read <= PreviewLimit)
                {
                    memory.Write(buffer, 0, read);
                    continue;
                }

                if (overflow is null)
                {
                    overflow = new FileStream(
                        Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920,
                        FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                    preview = memory.ToArray();
                    await overflow.WriteAsync(preview, ct);
                }
                await overflow.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch
        {
            overflow?.Dispose();
            throw;
        }

        var contentType = response.Content.Headers.ContentType;
        return new ResponseBody(preview ?? memory.ToArray(), total, overflow, contentType?.MediaType, contentType?.CharSet);
    }

    public void Dispose()
    {
        _strict.Dispose();
        _permissive.Dispose();
    }
}
