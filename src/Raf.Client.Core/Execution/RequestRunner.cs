using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Raf.Client.Core.Local;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>El request no se puede armar. El mensaje no incluye valores de variables.</summary>
public sealed class RequestBuildException(string message) : Exception(message);

/// <summary>Ejecuta requests desde el cliente: merge del sample, resolución de variables y envío.</summary>
public sealed class RequestRunner : IDisposable
{
    /// <summary>Tope de lo que se conserva en memoria y se muestra de un body.</summary>
    public const int PreviewLimit = 2 * 1024 * 1024;

    /// <summary>Mismo tope que usaba HttpClient al seguir las redirecciones solo.</summary>
    public const int MaxRedirects = 50;

    private readonly HttpClient _strict = CreateClient(acceptInvalidCertificates: false);
    private readonly HttpClient _permissive = CreateClient(acceptInvalidCertificates: true);

    private static HttpClient CreateClient(bool acceptInvalidCertificates)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false, // cada request envía solo las cookies que declara
            AllowAutoRedirect = false, // las sigue RunAsync, para mostrar cada una en la consola
            ConnectCallback = RunTrace.ConnectAsync,
            PlaintextStreamFilter = RunTrace.ConnectionReady,
        };
        if (acceptInvalidCertificates)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Lo que se envía: el request con los overrides del sample y, si la auth queda en
    /// <see cref="AuthType.Inherit"/>, la de la App (o ninguna, si no se conoce).
    /// </summary>
    public static RequestSpec Effective(RequestSpec request, SampleOverrides? sample, AuthSpec? appAuth = null)
    {
        var merged = sample is null ? request : SampleMerger.Merge(request, sample);
        if (merged.Auth.Type == AuthType.Inherit)
            merged = merged with { Auth = appAuth is { Type: not AuthType.Inherit } app ? app : AuthSpec.None };
        return ActiveParts(merged);
    }

    /// <summary>La auth efectiva sale de la App.</summary>
    public static bool InheritsAuth(RequestSpec request, SampleOverrides? sample) =>
        (sample?.Auth ?? request.Auth).Type == AuthType.Inherit;

    /// <summary>Informa las variables sin resolver, solo de las partes del request que se van a enviar.</summary>
    public static RunPreflight Check(RequestSpec request, SampleOverrides? sample, VariableContext variables, AuthSpec? appAuth = null)
    {
        var resolver = variables.CreateResolver();
        resolver.Resolve(Effective(request, sample, appAuth));
        var unresolved = resolver.Missing.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new RunPreflight(
            unresolved.Where(k => !variables.IsUnreadable(k)).ToList(),
            unresolved.Where(variables.IsUnreadable).ToList());
    }

    /// <summary>URL para el historial, sin valores de variables secretas ni de la autenticación.</summary>
    public static string DescribeUrl(RequestSpec request, SampleOverrides? sample, VariableContext variables, AuthSpec? appAuth = null)
    {
        var spec = variables.CreatePublicResolver().Resolve(Effective(request, sample, appAuth));
        return AppendQuery(spec.Url.Trim(), spec.QueryParams);
    }

    public async Task<RunResult> RunAsync(RunInput input, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var trace = new RunTrace(input.Variables, input.Options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(input.Options.Timeout);
        var messages = new List<HttpRequestMessage>();
        HttpResponseMessage? response = null;

        try
        {
            var resolver = input.Variables.CreateResolver();
            var spec = resolver.Resolve(Effective(input.Request, input.Sample, input.AppAuth));
            if (resolver.Missing.Count > 0) trace.MissingVariables(resolver.Missing);
            if (InheritsAuth(input.Request, input.Sample))
                trace.Info(RunEventKind.Auth, spec.Auth.Type == AuthType.None
                    ? CoreTexts.Trace_AuthInheritedNone
                    : string.Format(CoreTexts.Trace_AuthInherited, spec.Auth.Type));

            var message = BuildMessage(spec);
            messages.Add(message);
            var body = message.Content is null ? null : await message.Content.ReadAsStringAsync(timeout.Token);
            var client = input.Options.AcceptInvalidCertificates ? _permissive : _strict;

            for (var redirects = 0; ; redirects++)
            {
                // Se lee por partes para que el timeout cubra también la descarga del body.
                trace.BeginHop(message, body);
                response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                trace.EndHop(response);

                if (RedirectTarget(message.RequestUri!, response, trace) is not { } target) break;
                if (redirects == MaxRedirects)
                {
                    trace.Warn(RunEventKind.Redirect, string.Format(CoreTexts.Run_MaxRedirects, MaxRedirects));
                    break;
                }

                var next = FollowRedirect(message, response.StatusCode, target);
                messages.Add(next);
                trace.Redirected(message, next, response.StatusCode);
                if (next.Content is null) body = null;
                response.Dispose();
                response = null;
                message = next;
            }

            trace.BeginBody();
            var responseBody = await ReadBodyAsync(response!, timeout.Token);
            trace.BodyRead(responseBody);
            stopwatch.Stop();

            var headers = response!.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v))).ToList();
            var extracted = Extract(spec.Extractions, (int)response.StatusCode, headers, responseBody, trace);

            return new RunResult
            {
                Outcome = RunOutcome.Completed,
                Elapsed = stopwatch.Elapsed,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = headers,
                Body = responseBody,
                Events = trace.Finish(),
                Extracted = extracted,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            trace.Fail(RunEventLevel.Warning, CoreTexts.Run_CancelledTrace);
            return new RunResult
            {
                Outcome = RunOutcome.Cancelled, Elapsed = stopwatch.Elapsed, Error = CoreTexts.Run_Cancelled, Events = trace.Finish(),
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var error = string.Format(CoreTexts.Run_Timeout, input.Options.Timeout.TotalSeconds.ToString("0.#"));
            trace.Fail(RunEventLevel.Error, string.Format(CoreTexts.Run_TimeoutTrace, error));
            return new RunResult { Outcome = RunOutcome.TimedOut, Elapsed = stopwatch.Elapsed, Error = error, Events = trace.Finish() };
        }
        catch (Exception e) when (e is HttpRequestException or RequestBuildException or IOException)
        {
            var error = input.Variables.Scrub(Describe(e));
            trace.Fail(RunEventLevel.Error, error, e is RequestBuildException ? null : e);
            return new RunResult { Outcome = RunOutcome.Failed, Elapsed = stopwatch.Elapsed, Error = error, Events = trace.Finish() };
        }
        finally
        {
            response?.Dispose();
            foreach (var m in messages) m.Dispose(); // comparten el contenido: se libera recién al final
        }
    }

    /// <summary>
    /// Destino de una redirección, con las reglas que aplicaba HttpClient al seguirlas solo: Location relativo o
    /// absoluto, se conserva el fragmento y no se pasa de HTTPS a HTTP.
    /// </summary>
    private static Uri? RedirectTarget(Uri from, HttpResponseMessage response, RunTrace trace)
    {
        if ((int)response.StatusCode is not (300 or 301 or 302 or 303 or 307 or 308)) return null;
        if (response.Headers.Location is not { } location)
        {
            trace.Warn(RunEventKind.Redirect, CoreTexts.Run_RedirectNoLocation);
            return null;
        }

        if (!location.IsAbsoluteUri) location = new Uri(from, location);
        if (from.Fragment.Length > 0 && location.Fragment.Length == 0)
            location = new UriBuilder(location) { Fragment = from.Fragment[1..] }.Uri;

        if (location.Scheme != Uri.UriSchemeHttp && location.Scheme != Uri.UriSchemeHttps)
        {
            trace.Warn(RunEventKind.Redirect, string.Format(CoreTexts.Run_RedirectScheme, location.Scheme));
            return null;
        }
        if (from.Scheme == Uri.UriSchemeHttps && location.Scheme == Uri.UriSchemeHttp)
        {
            trace.Warn(RunEventKind.Redirect, string.Format(CoreTexts.Run_RedirectDowngrade, location.AbsoluteUri));
            return null;
        }
        return location;
    }

    /// <summary>
    /// El request siguiente de una redirección. Como HttpClient: se quita Authorization; 301, 302 y 300 pasan un
    /// POST a GET y 303 pasa todo a GET salvo HEAD, sin body; 307 y 308 repiten método y body.
    /// </summary>
    private static HttpRequestMessage FollowRedirect(HttpRequestMessage from, HttpStatusCode status, Uri target)
    {
        var method = from.Method;
        var toGet = (int)status switch
        {
            300 or 301 or 302 => method == HttpMethod.Post,
            303 => method != HttpMethod.Get && method != HttpMethod.Head,
            _ => false,
        };

        var next = new HttpRequestMessage(toGet ? HttpMethod.Get : method, target)
        {
            Version = from.Version,
            VersionPolicy = from.VersionPolicy,
            Content = toGet ? null : from.Content,
        };
        foreach (var (name, values) in from.Headers.NonValidated)
            if (!name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                next.Headers.TryAddWithoutValidation(name, values);
        return next;
    }

    private static string Describe(Exception e) =>
        e is HttpRequestException { InnerException: { } inner } ? $"{e.Message} ({inner.Message})" : e.Message;

    /// <summary>
    /// Aplica las extracciones activas si la respuesta es 2xx. La consola dice qué se guardó, sin mostrar el valor:
    /// las variables de sesión se tratan como secretas.
    /// </summary>
    private static Dictionary<string, string> Extract(
        IReadOnlyList<ResponseExtraction>? extractions, int status, IReadOnlyList<KeyValuePair<string, string>> headers,
        ResponseBody body, RunTrace trace)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var active = (extractions ?? []).Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Variable)).ToList();
        if (active.Count == 0) return values;
        if (status is < 200 or >= 300)
        {
            trace.Warn(RunEventKind.Extraction, string.Format(CoreTexts.Trace_ExtractSkipped, status));
            return values;
        }

        var text = active.Any(e => e.Source == ExtractionSource.Body) ? body.GetPreviewText() : null;
        foreach (var extraction in active)
        {
            var name = extraction.Variable.Trim();
            var where = extraction.Source == ExtractionSource.Header
                ? string.Format(CoreTexts.Trace_ExtractFromHeader, extraction.Path.Trim())
                : string.Format(CoreTexts.Trace_ExtractFromBody, extraction.Path.Trim());
            if (!SessionVariableStore.IsValidName(name))
            {
                trace.Warn(RunEventKind.Extraction, string.Format(CoreTexts.Session_InvalidName, name), where);
                continue;
            }

            var outcome = ResponseExtractor.Evaluate(extraction, text, headers);
            if (outcome.Value is { } value)
            {
                values[name] = value;
                trace.Info(RunEventKind.Extraction, string.Format(CoreTexts.Trace_Extracted, name), where);
            }
            else
            {
                trace.Warn(RunEventKind.Extraction, string.Format(CoreTexts.Trace_ExtractFailed, name), where + Environment.NewLine + outcome.Problem);
            }
        }
        if (body.IsTruncated && text is not null)
            trace.Warn(RunEventKind.Extraction, CoreTexts.Trace_ExtractTruncated);
        return values;
    }

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
            AuthType.None or AuthType.Inherit => AuthSpec.None,
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
        if (string.IsNullOrWhiteSpace(spec.Method)) throw new RequestBuildException(CoreTexts.Run_MissingMethod);

        HttpMethod method;
        try
        {
            method = new HttpMethod(spec.Method.Trim().ToUpperInvariant());
        }
        catch (FormatException)
        {
            throw new RequestBuildException(CoreTexts.Run_InvalidMethod);
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
        if (text.Length == 0) throw new RequestBuildException(CoreTexts.Run_MissingUrl);
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new RequestBuildException(CoreTexts.Run_InvalidUrl);
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
        _ => throw new RequestBuildException(CoreTexts.Run_UnsupportedBody),
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
                throw new RequestBuildException(string.Format(CoreTexts.Run_InvalidHeader, key));

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
                    throw new RequestBuildException(CoreTexts.Run_InvalidApiKeyName);
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
