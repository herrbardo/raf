using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using Raf.Client.Core.Api;
using Raf.Contracts;

namespace Raf.Client.Core.Execution;

/// <summary>
/// Envía cada salto HTTP por el relay del servidor RAF (<c>POST /relay</c>), para el cliente web: desde el navegador
/// no se puede llamar a cualquier API (CORS, headers prohibidos, certificados). Devuelve la respuesta del destino como
/// si hubiera llegado directo y completa la traza de la conexión con los tiempos que midió el servidor, así
/// <see cref="RequestRunner"/> funciona igual que en desktop.
/// </summary>
public sealed class RelayHandler(RafApiClient api, bool acceptInvalidCertificates) : HttpMessageHandler
{
    /// <summary>Header que se agrega a la respuesta si el servidor cortó el body; trae el largo original.</summary>
    public const string TruncatedHeader = "X-Raf-Relay-Truncated";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var body = request.Content is null ? null : Convert.ToBase64String(await request.Content.ReadAsByteArrayAsync(ct));
        var headers = request.Headers.NonValidated
            .Concat(request.Content?.Headers.NonValidated ?? default)
            .SelectMany(h => h.Value.Select(v => new RelayHeader(h.Key, v)))
            .ToList();
        var timeout = request.Options.TryGetValue(RequestRunner.TimeoutKey, out var t) ? t : TimeSpan.FromSeconds(30);
        var relay = new RelayRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, body,
            (int)Math.Ceiling(timeout.TotalSeconds), acceptInvalidCertificates);

        RelayResponse result;
        try
        {
            result = await api.PostAsync<RelayResponse>("relay", relay, ct);
        }
        catch (ApiException e) when (e is not SessionExpiredException and not ClientOutdatedException)
        {
            // Para la consola del request es un fallo de red más, con el motivo que dio el servidor.
            throw new HttpRequestException(string.Format(CoreTexts.Run_RelayFailed, e.Message));
        }

        if (result.Connection is { } connection && request.Options.TryGetValue(ConnectionTrace.Key, out var trace))
            Describe(trace, connection, start);
        return ToResponse(request, result);
    }

    private static HttpResponseMessage ToResponse(HttpRequestMessage request, RelayResponse result)
    {
        var response = new HttpResponseMessage((HttpStatusCode)result.StatusCode)
        {
            ReasonPhrase = result.ReasonPhrase,
            Version = Version.TryParse(result.Version, out var version) ? version : HttpVersion.Version11,
            RequestMessage = request,
            Content = new ByteArrayContent(Convert.FromBase64String(result.Body)),
        };
        foreach (var header in result.Headers)
        {
            // Con el body cortado, el largo declarado ya no coincide con lo que hay.
            if (result.Truncated && header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!response.Headers.TryAddWithoutValidation(header.Name, header.Value))
                response.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }
        if (result.Truncated)
            response.Headers.TryAddWithoutValidation(TruncatedHeader, result.BodyLength.ToString());
        return response;
    }

    /// <summary>Ubica las etapas que midió el servidor a partir del inicio del envío.</summary>
    private static void Describe(ConnectionTrace trace, RelayConnection c, long start)
    {
        static long After(long from, double ms) => from + (long)(ms * Stopwatch.Frequency / 1000);

        var dnsEnd = After(start, c.DnsMs);
        var connectEnd = After(dnsEnd, c.ConnectMs);
        var readyEnd = After(connectEnd, c.TlsMs ?? 0);
        trace.Update(i =>
        {
            (i.Host, i.Port, i.Remote) = (c.Host, c.Port, c.Remote);
            i.Addresses = c.Addresses.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>().ToArray();
            (i.DnsStart, i.DnsEnd, i.ConnectEnd, i.ReadyEnd) = (start, dnsEnd, connectEnd, readyEnd);
            i.TlsProtocol = Enum.TryParse<SslProtocols>(c.TlsProtocol, out var protocol) ? protocol : null;
            i.Cipher = Enum.TryParse<TlsCipherSuite>(c.Cipher, out var cipher) ? cipher : null;
            (i.CertificateSubject, i.CertificateIssuer, i.CertificateExpires) = (c.CertificateSubject, c.CertificateIssuer, c.CertificateExpires);
        });
    }
}
