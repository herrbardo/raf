using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Raf.Contracts;

namespace Raf.Server.Relay;

public sealed class RelayOptions
{
    public const string Section = "Relay";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Nombres de host que pueden resolver a direcciones internas, para un servidor que tiene que alcanzar una API
    /// de su propia red. Por defecto ninguno.
    /// </summary>
    public List<string> AllowedPrivateHosts { get; set; } = [];

    public int MaxRequestBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxResponseBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxTimeoutSeconds { get; set; } = 120;

    /// <summary>Envíos por minuto de cada usuario.</summary>
    public int PerMinute { get; set; } = 120;
}

/// <summary>Un relay que no se pudo completar. <see cref="Kind"/> es uno de <see cref="RelayErrors"/>.</summary>
public sealed class RelayException(string kind, int status, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Kind { get; } = kind;
    public int Status { get; } = status;
}

/// <summary>El destino resolvió a una dirección interna.</summary>
internal sealed class RelayBlockedException(string host) : Exception(
    $"'{host}' es una dirección interna (localhost o red privada) y el servidor no permite enviarle requests.")
{
    public string Host { get; } = host;
}

/// <summary>
/// Envía los saltos del relay. Como en desktop: sin redirecciones, sin cookies y con la traza de la conexión. Sin
/// proxy, porque un proxy recibiría la conexión sin pasar por el control de direcciones, que se hace al conectar:
/// se valida la IP resuelta y se conecta a esa misma IP, para que no cambie entre el control y la conexión.
/// </summary>
public sealed class RelaySender : IDisposable
{
    private static readonly HttpRequestOptionsKey<Trace> TraceKey = new("Raf.RelayTrace");

    private readonly IOptions<RelayOptions> _options;
    private readonly HttpClient _strict;
    private readonly HttpClient _permissive;

    public RelaySender(IOptions<RelayOptions> options)
    {
        _options = options;
        _strict = CreateClient(acceptInvalidCertificates: false);
        _permissive = CreateClient(acceptInvalidCertificates: true);
    }

    private HttpClient CreateClient(bool acceptInvalidCertificates)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectAsync,
            PlaintextStreamFilter = ConnectionReady,
        };
        if (acceptInvalidCertificates)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<RelayResponse> SendAsync(RelayRequest request, CancellationToken ct)
    {
        var options = _options.Value;
        using var message = Build(request, options);
        var trace = new Trace();
        message.Options.Set(TraceKey, trace);

        // Un margen sobre el tiempo del cliente, para que el timeout lo marque el cliente, como en desktop.
        var seconds = Math.Clamp(request.TimeoutSeconds, 1, options.MaxTimeoutSeconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 5));

        var client = request.AcceptInvalidCertificates ? _permissive : _strict;
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var (body, truncated) = await ReadBodyAsync(response, options.MaxResponseBytes, timeout.Token);
            var headers = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new RelayHeader(h.Key, v)))
                .ToList();
            return new RelayResponse(
                (int)response.StatusCode, response.ReasonPhrase, response.Version.ToString(2), headers,
                Convert.ToBase64String(body), response.Content.Headers.ContentLength ?? body.Length, truncated,
                trace.ToContract());
        }
        catch (Exception e) when (Find<RelayBlockedException>(e) is { } blocked)
        {
            throw new RelayException(RelayErrors.Blocked, StatusCodes.Status403Forbidden, blocked.Message);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new RelayException(RelayErrors.Timeout, StatusCodes.Status504GatewayTimeout,
                $"El destino no respondió en {seconds} s.", e);
        }
        catch (HttpRequestException e)
        {
            throw new RelayException(RelayErrors.Unreachable, StatusCodes.Status502BadGateway, Describe(e), e);
        }
    }

    private static HttpRequestMessage Build(RelayRequest request, RelayOptions options)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new RelayException(RelayErrors.Invalid, StatusCodes.Status400BadRequest, "La URL tiene que ser http o https y absoluta.");

        HttpMethod method;
        try
        {
            method = new HttpMethod(request.Method); // valida que sea un token HTTP
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new RelayException(RelayErrors.Invalid, StatusCodes.Status400BadRequest, $"'{request.Method}' no es un método HTTP válido.");
        }

        byte[]? body = null;
        if (request.Body is { Length: > 0 } encoded)
        {
            // Antes de decodificar: el base64 ocupa 4/3 del original.
            if ((long)encoded.Length * 3 / 4 > options.MaxRequestBytes + 2)
                throw TooLarge(options);
            try
            {
                body = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw new RelayException(RelayErrors.Invalid, StatusCodes.Status400BadRequest, "El body no está en base64.");
            }
            if (body.Length > options.MaxRequestBytes) throw TooLarge(options);
        }

        var message = new HttpRequestMessage(method, uri);
        if (body is not null) message.Content = new ByteArrayContent(body);
        foreach (var header in request.Headers)
        {
            if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue; // lo calcula el handler
            if (message.Headers.TryAddWithoutValidation(header.Name, header.Value)) continue;
            message.Content ??= new ByteArrayContent([]);
            message.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }
        return message;
    }

    private static RelayException TooLarge(RelayOptions options) =>
        new(RelayErrors.TooLarge, StatusCodes.Status413PayloadTooLarge,
            $"El body supera el máximo que acepta el servidor ({options.MaxRequestBytes / 1024 / 1024} MB).");

    private static async Task<(byte[] Body, bool Truncated)> ReadBodyAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max + 1L - buffer.Length)), ct);
            if (read == 0) return (buffer.ToArray(), false);
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max) return (buffer.GetBuffer().AsSpan(0, max).ToArray(), true);
        }
    }

    private static T? Find<T>(Exception? e) where T : Exception
    {
        for (; e is not null; e = e.InnerException)
            if (e is T match) return match;
        return null;
    }

    /// <summary>El mensaje y la causa, como los muestra la consola de desktop.</summary>
    private static string Describe(HttpRequestException e)
    {
        var parts = new List<string>();
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            var extra = x switch
            {
                HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } h => $" [{h.HttpRequestError}]",
                SocketException s => $" [{s.SocketErrorCode}]",
                _ => "",
            };
            parts.Add(x.Message + extra);
        }
        return string.Join(" → ", parts.Distinct());
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        context.InitialRequestMessage.Options.TryGetValue(TraceKey, out var trace);
        var endpoint = context.DnsEndPoint;
        var dnsStart = Stopwatch.GetTimestamp();

        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        var dnsEnd = Stopwatch.GetTimestamp();

        var host = endpoint.Host.Trim('[', ']');
        var allowPrivate = _options.Value.AllowedPrivateHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        var allowed = allowPrivate ? addresses : addresses.Where(RelayAddressPolicy.IsPublic).ToArray();
        if (allowed.Length == 0) throw new RelayBlockedException(host);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var remote = socket.RemoteEndPoint is IPEndPoint { Address.IsIPv4MappedToIPv6: true } mapped
            ? new IPEndPoint(mapped.Address.MapToIPv4(), mapped.Port)
            : socket.RemoteEndPoint;
        trace?.Connected(endpoint.Host, endpoint.Port, allowed, remote?.ToString(), dnsStart, dnsEnd, Stopwatch.GetTimestamp());
        return new NetworkStream(socket, ownsSocket: true);
    }

    private static ValueTask<Stream> ConnectionReady(SocketsHttpPlaintextStreamFilterContext context, CancellationToken ct)
    {
        if (context.InitialRequestMessage.Options.TryGetValue(TraceKey, out var trace))
            trace.Ready(context.PlaintextStream as SslStream, Stopwatch.GetTimestamp());
        return ValueTask.FromResult(context.PlaintextStream);
    }

    public void Dispose()
    {
        _strict.Dispose();
        _permissive.Dispose();
    }

    /// <summary>Lo completa el handler desde otro hilo al abrir una conexión nueva.</summary>
    private sealed class Trace
    {
        private readonly Lock _gate = new();
        private RelayConnection? _connection;
        private long _connectEnd;

        public void Connected(string host, int port, IPAddress[] addresses, string? remote, long dnsStart, long dnsEnd, long connectEnd)
        {
            lock (_gate)
            {
                _connectEnd = connectEnd;
                _connection = new RelayConnection(host, port, addresses.Select(a => a.ToString()).ToList(), remote,
                    Stopwatch.GetElapsedTime(dnsStart, dnsEnd).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(dnsEnd, connectEnd).TotalMilliseconds,
                    null, null, null, null, null, null);
            }
        }

        public void Ready(SslStream? ssl, long now)
        {
            var certificate = ssl?.RemoteCertificate;
            lock (_gate)
            {
                if (_connection is null || ssl is null) return;
                _connection = _connection with
                {
                    TlsMs = Stopwatch.GetElapsedTime(_connectEnd, now).TotalMilliseconds,
                    TlsProtocol = ssl.SslProtocol.ToString(),
                    Cipher = ssl.NegotiatedCipherSuite.ToString(),
                    CertificateSubject = certificate?.Subject,
                    CertificateIssuer = certificate?.Issuer,
                    CertificateExpires = (certificate as X509Certificate2)?.NotAfter,
                };
            }
        }

        public RelayConnection? ToContract()
        {
            lock (_gate) return _connection;
        }
    }
}
