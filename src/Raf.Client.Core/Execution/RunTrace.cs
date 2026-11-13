using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Raf.Client.Core.Execution;

public enum RunEventLevel { Info, Warning, Error }

public enum RunEventKind { Variables, Request, Dns, Connect, Tls, Reused, Response, Redirect, Body, Error, Summary }

/// <summary>
/// Un paso de una ejecución, para la consola de la pestaña. <see cref="At"/> es el momento desde el inicio y
/// <see cref="Duration"/>, lo que tardó la etapa. Los textos ya vienen con los secretos enmascarados.
/// </summary>
public sealed record RunEvent(TimeSpan At, TimeSpan? Duration, RunEventKind Kind, RunEventLevel Level, string Title, string? Detail = null);

/// <summary>Lo que el handler HTTP informa al abrir una conexión nueva para un request.</summary>
internal sealed class ConnectionInfo
{
    public string Host = "";
    public int Port;
    public long DnsStart, DnsEnd, ConnectEnd, ReadyEnd;
    public IPAddress[] Addresses = [];
    public string? Remote;
    public SslProtocols? TlsProtocol;
    public TlsCipherSuite? Cipher;
    public string? CertificateSubject, CertificateIssuer;
    public DateTime? CertificateExpires;

    public ConnectionInfo Copy() => (ConnectionInfo)MemberwiseClone();
}

/// <summary>
/// Se adjunta a cada <see cref="HttpRequestMessage"/>; el handler lo completa desde otro hilo mientras abre la
/// conexión, por eso se lee con una copia.
/// </summary>
internal sealed class ConnectionTrace
{
    public static readonly HttpRequestOptionsKey<ConnectionTrace> Key = new("Raf.ConnectionTrace");

    private readonly Lock _gate = new();
    private readonly ConnectionInfo _info = new();

    public void Update(Action<ConnectionInfo> change)
    {
        lock (_gate) change(_info);
    }

    public ConnectionInfo Snapshot()
    {
        lock (_gate) return _info.Copy();
    }
}

/// <summary>Arma los eventos de una ejecución. No se guarda: vive en el resultado mientras la pestaña lo muestra.</summary>
internal sealed class RunTrace(VariableContext variables, RunOptions options)
{
    /// <summary>Tope del body del request que se muestra en la consola.</summary>
    public const int BodyPreviewChars = 64 * 1024;

    private sealed record Hop(HttpRequestMessage Message, string? Body, long Start, ConnectionTrace Connection);

    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly List<RunEvent> _events = [];
    private Hop? _hop;
    private long _headersAt;
    private bool _readingBody;
    private int _redirects;
    private TimeSpan _dns, _connect, _tls, _wait, _download;

    public void MissingVariables(IEnumerable<string> missing)
    {
        var keys = missing.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var unreadable = keys.Where(variables.IsUnreadable).ToList();
        var undefined = keys.Except(unreadable).ToList();
        var lines = new List<string>();
        if (undefined.Count > 0) lines.Add(CoreTexts.Trace_Undefined + string.Join(", ", undefined));
        if (unreadable.Count > 0) lines.Add(CoreTexts.Trace_Unreadable + string.Join(", ", unreadable));
        Add(Stopwatch.GetTimestamp(), null, RunEventKind.Variables, RunEventLevel.Warning,
            CoreTexts.Trace_MissingVariables, string.Join(Environment.NewLine, lines));
    }

    public void BeginHop(HttpRequestMessage message, string? body)
    {
        var connection = new ConnectionTrace();
        message.Options.Set(ConnectionTrace.Key, connection);
        _hop = new Hop(message, body, Stopwatch.GetTimestamp(), connection);
    }

    /// <summary>Cierra el envío en curso: el request, la conexión y, si llegó, la respuesta.</summary>
    public void EndHop(HttpResponseMessage? response)
    {
        if (_hop is not { } hop) return;
        _hop = null;

        var message = hop.Message;
        Add(hop.Start, null, RunEventKind.Request, RunEventLevel.Info,
            $"{message.Method} {message.RequestUri!.AbsoluteUri}", DescribeRequest(hop, response?.Version));

        var c = hop.Connection.Snapshot();
        var ready = hop.Start;
        if (c.DnsStart != 0)
        {
            if (c.DnsEnd != 0)
            {
                var dns = Stopwatch.GetElapsedTime(c.DnsStart, c.DnsEnd);
                _dns += dns;
                Add(c.DnsStart, dns, RunEventKind.Dns, RunEventLevel.Info,
                    $"DNS: {c.Host} → {string.Join(", ", c.Addresses.Select(a => a.ToString()))}");
                ready = c.DnsEnd;
            }
            if (c.ConnectEnd != 0)
            {
                var connect = Stopwatch.GetElapsedTime(c.DnsEnd, c.ConnectEnd);
                _connect += connect;
                var proxy = !string.Equals(c.Host, message.RequestUri.IdnHost, StringComparison.OrdinalIgnoreCase);
                Add(c.DnsEnd, connect, RunEventKind.Connect, RunEventLevel.Info,
                    proxy ? string.Format(CoreTexts.Trace_ConnectProxy, $"{c.Host}:{c.Port}", c.Remote) : string.Format(CoreTexts.Trace_Connect, c.Remote));
                ready = c.ConnectEnd;
            }
            if (c.ReadyEnd != 0)
            {
                if (c.TlsProtocol is { } protocol)
                {
                    var tls = Stopwatch.GetElapsedTime(c.ConnectEnd, c.ReadyEnd);
                    _tls += tls;
                    Add(c.ConnectEnd, tls, RunEventKind.Tls, RunEventLevel.Info, $"Handshake {ProtocolName(protocol)}", DescribeTls(c));
                }
                ready = c.ReadyEnd;
            }
        }
        else if (response is not null)
        {
            Add(hop.Start, null, RunEventKind.Reused, RunEventLevel.Info, CoreTexts.Trace_Reused,
                CoreTexts.Trace_ReusedDetail);
        }

        if (response is null) return;
        _headersAt = Stopwatch.GetTimestamp();
        var wait = Stopwatch.GetElapsedTime(ready, _headersAt);
        _wait += wait;
        var code = (int)response.StatusCode;
        Add(_headersAt, wait, RunEventKind.Response, code >= 400 ? RunEventLevel.Warning : RunEventLevel.Info,
            string.Format(CoreTexts.Trace_Response, $"{code} {response.ReasonPhrase}".TrimEnd(), $"HTTP/{response.Version.ToString(2)}"), DescribeResponse(response));
    }

    public void Redirected(HttpRequestMessage from, HttpRequestMessage to, HttpStatusCode status)
    {
        _redirects++;
        var notes = new List<string>();
        if (from.Headers.Authorization is not null)
            notes.Add(CoreTexts.Trace_RedirectNoAuth);
        if (from.Content is not null && to.Content is null)
            notes.Add(string.Format(CoreTexts.Trace_RedirectToGet, (int)status, from.Method));
        Add(Stopwatch.GetTimestamp(), null, RunEventKind.Redirect, RunEventLevel.Info,
            string.Format(CoreTexts.Trace_Redirect, (int)status, $"{to.Method} {to.RequestUri!.AbsoluteUri}"),
            notes.Count == 0 ? null : string.Join(Environment.NewLine, notes));
    }

    public void Warn(RunEventKind kind, string title, string? detail = null) =>
        Add(Stopwatch.GetTimestamp(), null, kind, RunEventLevel.Warning, title, detail);

    public void BeginBody() => _readingBody = true;

    public void BodyRead(ResponseBody body)
    {
        _readingBody = false;
        var now = Stopwatch.GetTimestamp();
        var download = Stopwatch.GetElapsedTime(_headersAt, now);
        _download += download;
        var title = body.Length == 0
            ? CoreTexts.Trace_NoBody
            : string.Format(CoreTexts.Trace_BodyReceived, RunFormat.Size(body.Length)) + (body.MediaType is { } type ? $" ({type})" : "");
        Add(now, download, RunEventKind.Body, RunEventLevel.Info, title);
    }

    /// <summary>La ejecución terminó sin respuesta completa: se cierra lo que estaba en curso y se anota el motivo.</summary>
    public void Fail(RunEventLevel level, string title, Exception? error = null)
    {
        var stage = _hop is not null ? CoreTexts.Trace_StageBeforeResponse
            : _readingBody ? CoreTexts.Trace_StageDownload
            : null;
        EndHop(null);

        var detail = new List<string>();
        if (stage is not null) detail.Add(stage);
        for (var e = error; e is not null; e = e.InnerException)
        {
            var extra = e switch
            {
                HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } h => $" [{h.HttpRequestError}]",
                SocketException s => $" [{s.SocketErrorCode}]",
                AuthenticationException => " [TLS]",
                _ => "",
            };
            detail.Add($"{e.GetType().Name}{extra}: {e.Message}");
        }
        Add(Stopwatch.GetTimestamp(), null, RunEventKind.Error, level, title,
            detail.Count == 0 ? null : string.Join(Environment.NewLine, detail));
    }

    /// <summary>Agrega el total con los tiempos por etapa y devuelve los eventos.</summary>
    public IReadOnlyList<RunEvent> Finish()
    {
        var now = Stopwatch.GetTimestamp();
        var total = Stopwatch.GetElapsedTime(_start, now);
        var lines = new List<string>();
        void Stage(string name, TimeSpan time)
        {
            if (time > TimeSpan.Zero) lines.Add($"{name}: {RunFormat.Time(time)}");
        }
        Stage("DNS", _dns);
        Stage(CoreTexts.Trace_TimeConnect, _connect);
        Stage("TLS", _tls);
        Stage(CoreTexts.Trace_TimeWait, _wait);
        Stage(CoreTexts.Trace_TimeDownload, _download);
        if (_redirects > 0) lines.Add(string.Format(CoreTexts.Trace_Redirects, _redirects));
        Add(now, total, RunEventKind.Summary, RunEventLevel.Info, string.Format(CoreTexts.Trace_Total, RunFormat.Time(total)),
            lines.Count == 0 ? null : string.Join(Environment.NewLine, lines));
        return _events.ToList();
    }

    private void Add(long timestamp, TimeSpan? duration, RunEventKind kind, RunEventLevel level, string title, string? detail = null) =>
        _events.Add(new RunEvent(Stopwatch.GetElapsedTime(_start, timestamp), duration, kind, level,
            variables.Scrub(title), detail is null ? null : variables.Scrub(detail)));

    private string DescribeRequest(Hop hop, Version? version)
    {
        var message = hop.Message;
        var uri = message.RequestUri!;
        var text = new StringBuilder();
        text.Append(message.Method).Append(' ').Append(uri.PathAndQuery)
            .Append(" HTTP/").Append((version ?? message.Version).ToString(2)).AppendLine();
        text.Append("Host: ").AppendLine(uri.Authority);
        AppendHeaders(text, message.Headers.NonValidated);
        if (message.Content is { } content)
        {
            _ = content.Headers.ContentLength; // lo calcula al pedirlo, igual que al enviar
            AppendHeaders(text, content.Headers.NonValidated);
        }

        if (hop.Body is { Length: > 0 } body)
        {
            text.AppendLine();
            if (body.Length <= BodyPreviewChars) text.Append(body);
            else text.Append(body.AsSpan(0, BodyPreviewChars)).AppendLine().Append(string.Format(CoreTexts.Trace_BodyPreview, BodyPreviewChars / 1024));
        }
        return text.ToString().TrimEnd();
    }

    private string DescribeResponse(HttpResponseMessage response)
    {
        var text = new StringBuilder();
        text.Append("HTTP/").Append(response.Version.ToString(2)).Append(' ').Append((int)response.StatusCode)
            .Append(' ').AppendLine(response.ReasonPhrase);
        AppendHeaders(text, response.Headers.NonValidated);
        AppendHeaders(text, response.Content.Headers.NonValidated);
        return text.ToString().TrimEnd();
    }

    private void AppendHeaders(StringBuilder text, HttpHeadersNonValidated headers)
    {
        foreach (var (name, values) in headers)
            foreach (var value in values)
                text.Append(name).Append(": ").AppendLine(IsBasic(name, value) ? MaskBasic(value) : value);
    }

    private static bool IsBasic(string name, string value) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Basic va en base64: el enmascarado común no ve los secretos que tenga adentro.</summary>
    private string MaskBasic(string value)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
            return variables.Scrub(decoded) == decoded ? value : "Basic *** " + CoreTexts.Trace_BasicMasked;
        }
        catch (FormatException)
        {
            return value;
        }
    }

    private string DescribeTls(ConnectionInfo c)
    {
        var lines = new List<string>();
        if (c.Cipher is { } cipher) lines.Add(string.Format(CoreTexts.Trace_Cipher, cipher));
        if (c.CertificateSubject is { } subject) lines.Add(string.Format(CoreTexts.Trace_Certificate, subject));
        if (c.CertificateIssuer is { } issuer) lines.Add(string.Format(CoreTexts.Trace_Issuer, issuer));
        if (c.CertificateExpires is { } expires) lines.Add(string.Format(CoreTexts.Trace_Expires, expires.ToString("yyyy-MM-dd HH:mm")));
        if (options.AcceptInvalidCertificates) lines.Add(CoreTexts.Trace_InsecureTls);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ProtocolName(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        _ => protocol.ToString(),
    };

    /// <summary>Lo llama el handler al abrir la conexión TCP de un request.</summary>
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        context.InitialRequestMessage.Options.TryGetValue(ConnectionTrace.Key, out var trace);
        var endpoint = context.DnsEndPoint;
        var dnsStart = Stopwatch.GetTimestamp();
        trace?.Update(i => (i.Host, i.Port, i.DnsStart) = (endpoint.Host, endpoint.Port, dnsStart));

        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        var dnsEnd = Stopwatch.GetTimestamp();
        trace?.Update(i => (i.Addresses, i.DnsEnd) = (addresses, dnsEnd));

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var connectEnd = Stopwatch.GetTimestamp();
        var remote = socket.RemoteEndPoint is IPEndPoint { Address.IsIPv4MappedToIPv6: true } mapped
            ? new IPEndPoint(mapped.Address.MapToIPv4(), mapped.Port)
            : socket.RemoteEndPoint;
        trace?.Update(i => (i.Remote, i.ConnectEnd) = (remote?.ToString(), connectEnd));
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Lo llama el handler con la conexión lista, después del handshake TLS si lo hubo.</summary>
    internal static ValueTask<Stream> ConnectionReady(SocketsHttpPlaintextStreamFilterContext context, CancellationToken ct)
    {
        if (context.InitialRequestMessage.Options.TryGetValue(ConnectionTrace.Key, out var trace))
        {
            var now = Stopwatch.GetTimestamp();
            var ssl = context.PlaintextStream as SslStream;
            var certificate = ssl?.RemoteCertificate;
            trace.Update(i =>
            {
                i.ReadyEnd = now;
                if (ssl is null) return;
                i.TlsProtocol = ssl.SslProtocol;
                i.Cipher = ssl.NegotiatedCipherSuite;
                i.CertificateSubject = certificate?.Subject;
                i.CertificateIssuer = certificate?.Issuer;
                i.CertificateExpires = (certificate as X509Certificate2)?.NotAfter;
            });
        }
        return ValueTask.FromResult(context.PlaintextStream);
    }
}

/// <summary>Formatos de tiempo y tamaño que comparten la respuesta y su consola.</summary>
public static class RunFormat
{
    public static string Time(TimeSpan t) =>
        t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.00} s" : $"{t.TotalMilliseconds:0} ms";

    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B",
    };
}
