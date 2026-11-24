using System.Diagnostics;
using System.Text;
using Raf.Client.Core.Grpc;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>Requests gRPC (Plan 10): se arma la metadata y la llamada la hace <see cref="IGrpcEngine"/>.</summary>
public sealed partial class RequestRunner
{
    /// <summary>Habla gRPC; null en el cliente web, donde un request gRPC no se puede enviar.</summary>
    public IGrpcEngine? Grpc => _grpc;

    /// <summary>
    /// La dirección del servidor gRPC. Tiene que decir si usa TLS: <c>https://</c> o <c>grpcs://</c> con TLS,
    /// <c>http://</c> o <c>grpc://</c> sin TLS. Se descarta la ruta: la de la llamada es la del método.
    /// </summary>
    public static Uri GrpcAddress(string url)
    {
        var text = url.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal) is > 0 and var i ? text[..i].ToLowerInvariant() : "";
        var mapped = scheme switch
        {
            "http" or "grpc" => Uri.UriSchemeHttp,
            "https" or "grpcs" => Uri.UriSchemeHttps,
            _ => null,
        };
        if (mapped is null || !Uri.TryCreate(mapped + text[scheme.Length..], UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            throw new RequestBuildException(CoreTexts.Run_GrpcAddress);
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    /// <summary>
    /// La metadata que se envía: los headers activos y la autenticación (Bearer y Basic como <c>authorization</c>,
    /// la API key como su propia clave). Las claves van en minúscula, como pide gRPC.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GrpcMetadata(RequestSpec spec)
    {
        var metadata = spec.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h.Key))
            .Select(h => KeyValuePair.Create(h.Key.Trim().ToLowerInvariant(), h.Value))
            .ToList();

        void Set(string key, string value)
        {
            metadata.RemoveAll(m => m.Key == key);
            metadata.Add(KeyValuePair.Create(key, value));
        }

        switch (spec.Auth)
        {
            case { Type: AuthType.Bearer } bearer:
                Set("authorization", $"Bearer {bearer.Token}");
                break;
            case { Type: AuthType.Basic } basic:
                Set("authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.Username}:{basic.Password}")));
                break;
            case { Type: AuthType.ApiKey, ApiKeyIn: ApiKeyLocation.Header, ApiKeyName: { Length: > 0 } name } key:
                Set(name.Trim().ToLowerInvariant(), key.ApiKeyValue ?? "");
                break;
        }
        return metadata;
    }

    /// <summary>Una llamada gRPC unaria. Los errores de red, cancelación y timeout los atrapa <see cref="RunOnceAsync"/>.</summary>
    private async Task<RunResult> RunGrpcAsync(RunInput input, RequestSpec spec, RunTrace trace, Stopwatch stopwatch, CancellationToken ct)
    {
        if (_grpc is null) throw new RequestBuildException(CoreTexts.Run_GrpcUnavailable);
        if (spec.Grpc is not { IsComplete: true } grpc) throw new RequestBuildException(CoreTexts.Run_GrpcNoMethod);
        if (spec.Auth is { Type: AuthType.ApiKey, ApiKeyIn: ApiKeyLocation.Query })
            trace.Warn(RunEventKind.Auth, CoreTexts.Trace_GrpcApiKeyQuery);

        var address = GrpcAddress(spec.Url);
        var metadata = GrpcMetadata(spec);
        var message = string.IsNullOrWhiteSpace(spec.Body.Content) ? "{}" : spec.Body.Content;
        trace.GrpcCall(address, grpc.Path, metadata, message);

        var target = new GrpcTarget(address, input.Options.AcceptInvalidCertificates, metadata, input.Options.Timeout);
        var result = await _grpc.CallUnaryAsync(new GrpcCall(target, grpc.Service, grpc.Method, message), ct);
        trace.GrpcStatus(result.StatusCode, result.StatusName, result.StatusDetail, result.Headers, result.Trailers);
        stopwatch.Stop();

        var json = Encoding.UTF8.GetBytes(result.MessageJson ?? "");
        var body = new ResponseBody(json, json.Length, null, result.MessageJson is null ? null : "application/json", "utf-8");
        var headers = result.Headers.Concat(result.Trailers).ToList();

        // Las extracciones son para respuestas buenas: en gRPC, el estado OK hace las veces del 2xx.
        var extracted = result.IsOk
            ? Extract(spec.Extractions, 200, headers, body, trace)
            : new Dictionary<string, string>();

        return new RunResult
        {
            Outcome = RunOutcome.Completed,
            Elapsed = stopwatch.Elapsed,
            GrpcStatus = new GrpcStatusInfo(result.StatusCode, result.StatusName, result.StatusDetail),
            Headers = headers,
            Body = body,
            Events = trace.Finish(),
            Extracted = extracted,
        };
    }
}
