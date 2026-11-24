using System.Net;
using System.Net.WebSockets;
using System.Text;
using Raf.Client.Core.Execution;

namespace Raf.Client.Core.WebSockets;

/// <summary>Cómo abrir la conexión: la URL y los headers del handshake ya resueltos.</summary>
public sealed record WebSocketConnectOptions(
    Uri Uri,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    IReadOnlyList<string> Subprotocols,
    bool AcceptInvalidCertificates,
    TimeSpan Timeout);

public enum WebSocketDirection { Sent, Received }

/// <summary>
/// Un mensaje de la sesión. Uno binario se muestra en base64. <see cref="Truncated"/>: superaba
/// <see cref="WebSocketSession.MaxMessageSize"/> y se conservó solo el comienzo; <see cref="Size"/> es el tamaño real.
/// </summary>
public sealed record WebSocketFrame(DateTimeOffset At, WebSocketDirection Direction, bool IsBinary, string Text, long Size, bool Truncated = false);

/// <summary>Cómo terminó la sesión: el código y el motivo del cierre, quién la cerró, o el error si se cortó.</summary>
public sealed record WebSocketClosed(int? Status, string? Description, bool ByServer, string? Error);

/// <summary>El servidor no aceptó el WebSocket. Si respondió algo, viene el código HTTP y sus headers.</summary>
public sealed class WebSocketConnectException(string message, int? status, IReadOnlyList<KeyValuePair<string, string>> headers, Exception? inner)
    : Exception(message, inner)
{
    public int? Status { get; } = status;
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; } = headers;
}

/// <summary>
/// Una conexión WebSocket abierta. Los mensajes recibidos y el cierre llegan por eventos, desde otro hilo: quien los
/// muestre los tiene que pasar al hilo de la interfaz. Solo se usa en la app de escritorio.
/// </summary>
public sealed class WebSocketSession : IAsyncDisposable
{
    /// <summary>Lo que se conserva de un mensaje recibido; el resto se cuenta pero se descarta.</summary>
    public const int MaxMessageSize = 16 * 1024 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private Task? _receiving;
    private bool _closing;
    private int _closedRaised;

    private WebSocketSession(ClientWebSocket socket)
    {
        _socket = socket;
        Status = (int?)socket.HttpStatusCode is > 0 and var code ? code : 101;
        ResponseHeaders = Pairs(socket.HttpResponseHeaders);
        Subprotocol = socket.SubProtocol;
    }

    /// <summary>El código del handshake (101 si se aceptó).</summary>
    public int Status { get; }

    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; }

    /// <summary>El subprotocolo que eligió el servidor, si eligió uno.</summary>
    public string? Subprotocol { get; }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public event Action<WebSocketFrame>? Received;

    /// <summary>Se dispara una sola vez, la cierre quien la cierre.</summary>
    public event Action<WebSocketClosed>? Closed;

    /// <summary>
    /// Abre la conexión. Lanza <see cref="RequestBuildException"/> si un header no se puede enviar y
    /// <see cref="WebSocketConnectException"/> si el servidor no acepta o no se puede conectar.
    /// </summary>
    public static async Task<WebSocketSession> ConnectAsync(WebSocketConnectOptions options, CancellationToken ct = default)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        if (options.AcceptInvalidCertificates) socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        foreach (var protocol in options.Subprotocols.Where(p => !string.IsNullOrWhiteSpace(p))) socket.Options.AddSubProtocol(protocol.Trim());
        foreach (var (name, value) in options.Headers)
        {
            try
            {
                socket.Options.SetRequestHeader(name, value);
            }
            catch (ArgumentException e)
            {
                socket.Dispose();
                throw new RequestBuildException(string.Format(CoreTexts.Ws_InvalidHeader, name, e.Message));
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        try
        {
            await socket.ConnectAsync(options.Uri, timeout.Token);
            return new WebSocketSession(socket);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            var status = (int)socket.HttpStatusCode;
            var headers = Pairs(socket.HttpResponseHeaders);
            socket.Dispose();
            var message = e is OperationCanceledException
                ? string.Format(CoreTexts.Run_Timeout, options.Timeout.TotalSeconds.ToString("0.#"))
                : status > 0 && status != (int)HttpStatusCode.SwitchingProtocols
                    ? string.Format(CoreTexts.Ws_Rejected, status)
                    : Describe(e);
            throw new WebSocketConnectException(message, status > 0 ? status : null, headers, e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string Describe(Exception e)
    {
        var inner = e.InnerException;
        while (inner?.InnerException is not null) inner = inner.InnerException;
        return inner is null ? e.Message : $"{e.Message} ({inner.Message})";
    }

    private static List<KeyValuePair<string, string>> Pairs(IReadOnlyDictionary<string, IEnumerable<string>>? headers) =>
        headers?.SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v))).ToList() ?? [];

    /// <summary>Empieza a recibir. Se llama después de suscribirse a los eventos, para no perder nada.</summary>
    public void Start() => _receiving ??= Task.Run(ReceiveLoopAsync);

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                using var message = new MemoryStream();
                long size = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _life.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        RaiseClosed(new WebSocketClosed((int?)result.CloseStatus, result.CloseStatusDescription, !_closing, null));
                        if (!_closing && _socket.State == WebSocketState.CloseReceived)
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }
                    size += result.Count;
                    var room = MaxMessageSize - (int)message.Length;
                    if (room > 0) message.Write(buffer, 0, Math.Min(room, result.Count));
                }
                while (!result.EndOfMessage);

                var binary = result.MessageType == WebSocketMessageType.Binary;
                var bytes = message.ToArray();
                var text = binary ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);
                Received?.Invoke(new WebSocketFrame(DateTimeOffset.Now, WebSocketDirection.Received, binary, text, size, size > bytes.Length));
            }
        }
        catch (OperationCanceledException)
        {
            RaiseClosed(new WebSocketClosed(null, null, false, null));
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            RaiseClosed(new WebSocketClosed(null, null, true, _closing ? null : Describe(e)));
        }
    }

    private void RaiseClosed(WebSocketClosed closed)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(closed);
    }

    /// <summary>Manda un mensaje de texto y devuelve cómo quedó registrado.</summary>
    public async Task<WebSocketFrame> SendTextAsync(string text, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sending.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sending.Release();
        }
        return new WebSocketFrame(DateTimeOffset.Now, WebSocketDirection.Sent, false, text, bytes.Length);
    }

    /// <summary>Cierre ordenado: avisa al servidor y espera su respuesta un momento; si no llega, corta.</summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        _closing = true;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (_receiving is null)
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", wait.Token);
            }
            else
            {
                // La respuesta del servidor al cierre la recibe el bucle, que es el único que lee.
                if (_socket.State == WebSocketState.Open)
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", wait.Token);
                await _receiving.WaitAsync(wait.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or WebSocketException)
        {
            _socket.Abort();
            await _life.CancelAsync();
        }
        RaiseClosed(new WebSocketClosed((int)WebSocketCloseStatus.NormalClosure, null, false, null));
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await CloseAsync();
        await _life.CancelAsync();
        _socket.Dispose();
        _life.Dispose();
        _sending.Dispose();
    }
}
