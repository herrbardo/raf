using Google.Protobuf.Reflection;
using Grpc.Core;
using Raf.Client.Core.Grpc;

namespace Raf.Client.Grpc;

/// <summary>
/// Una llamada con streaming abierta (Plan 10, etapa 3): lee en segundo plano, traduce cada mensaje a JSON y avisa el
/// final con el estado, la metadata y los trailers. Los envíos se hacen de a uno.
/// </summary>
internal sealed class GrpcStream : IGrpcStream
{
    private readonly ProtoJson _json;
    private readonly MethodDescriptor _method;
    private readonly CancellationTokenSource _life;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly IDisposable _call;
    private readonly IClientStreamWriter<byte[]>? _requests;
    private readonly IAsyncStreamReader<byte[]>? _responses;
    private readonly Task<byte[]>? _response;
    private readonly Task<Metadata> _headers;
    private readonly Func<Status> _status;
    private readonly Func<Metadata> _trailers;
    private Task? _reading;
    private bool _sendingDone;
    private int _completed;

    private GrpcStream(GrpcMethodKind kind, ProtoJson json, MethodDescriptor method, CancellationTokenSource life, IDisposable call,
        Task<Metadata> headers, Func<Status> status, Func<Metadata> trailers)
    {
        Kind = kind;
        _json = json;
        _method = method;
        _life = life;
        _call = call;
        _headers = headers;
        _status = status;
        _trailers = trailers;
    }

    public GrpcStream(GrpcMethodKind kind, ProtoJson json, MethodDescriptor method, CancellationTokenSource life,
        AsyncServerStreamingCall<byte[]> call, long sentSize)
        : this(kind, json, method, life, call, call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers)
    {
        _responses = call.ResponseStream;
        _sendingDone = true;
        SentSize = sentSize;
    }

    public GrpcStream(GrpcMethodKind kind, ProtoJson json, MethodDescriptor method, CancellationTokenSource life,
        AsyncClientStreamingCall<byte[], byte[]> call)
        : this(kind, json, method, life, call, call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers)
    {
        _requests = call.RequestStream;
        _response = call.ResponseAsync;
    }

    public GrpcStream(GrpcMethodKind kind, ProtoJson json, MethodDescriptor method, CancellationTokenSource life,
        AsyncDuplexStreamingCall<byte[], byte[]> call)
        : this(kind, json, method, life, call, call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers)
    {
        _requests = call.RequestStream;
        _responses = call.ResponseStream;
    }

    public GrpcMethodKind Kind { get; }

    /// <summary>En server streaming, el tamaño del mensaje que se mandó al abrir.</summary>
    public long SentSize { get; }

    public bool CanSend => _requests is not null && !_sendingDone && _completed == 0;

    public event Action<GrpcStreamMessage>? Received;

    public event Action<GrpcCallResult>? Completed;

    public void Start() => _reading ??= Task.Run(ReadAsync);

    private async Task ReadAsync()
    {
        try
        {
            if (_responses is not null)
            {
                while (await _responses.MoveNext(_life.Token)) Deliver(_responses.Current);
            }
            else
            {
                Deliver(await _response!);
            }
            Complete(_status(), await HeadersAsync(), _trailers());
        }
        catch (RpcException e)
        {
            Complete(e.Status, await HeadersAsync(), e.Trailers);
        }
        catch (OperationCanceledException)
        {
            Complete(new Status(StatusCode.Cancelled, "Cancelado"), await HeadersAsync(), []);
        }
    }

    private void Deliver(byte[] data)
    {
        GrpcStreamMessage message;
        try
        {
            message = new GrpcStreamMessage(DateTimeOffset.Now, _json.Decode(data, _method.OutputType), data.Length);
        }
        catch (GrpcEngineException e)
        {
            message = new GrpcStreamMessage(DateTimeOffset.Now, null, data.Length, e.Message);
        }
        Received?.Invoke(message);
    }

    /// <summary>La metadata de respuesta, si llegó; si la llamada falló antes, no hay.</summary>
    private async Task<Metadata> HeadersAsync()
    {
        try
        {
            return await _headers;
        }
        catch (Exception e) when (e is RpcException or OperationCanceledException)
        {
            return [];
        }
    }

    private void Complete(Status status, Metadata headers, Metadata trailers)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) Completed?.Invoke(GrpcEngine.Result(status, headers, trailers, null));
    }

    public async Task<long> SendAsync(string json, CancellationToken ct = default)
    {
        if (_requests is null || _sendingDone) throw new InvalidOperationException("La llamada no acepta más mensajes.");
        var bytes = _json.Encode(json, _method.InputType);
        await _sending.WaitAsync(ct);
        try
        {
            await _requests.WriteAsync(bytes, ct);
        }
        catch (RpcException e)
        {
            throw new GrpcEngineException($"{GrpcEngine.StatusName(e.StatusCode)}: {e.Status.Detail}", e);
        }
        catch (InvalidOperationException e)
        {
            // La llamada ya terminó (el servidor cerró): el motivo llega por Completed.
            throw new GrpcEngineException(e.Message, e);
        }
        finally
        {
            _sending.Release();
        }
        return bytes.Length;
    }

    public async Task CompleteSendingAsync()
    {
        if (_requests is null || _sendingDone) return;
        _sendingDone = true;
        await _sending.WaitAsync();
        try
        {
            await _requests.CompleteAsync();
        }
        catch (Exception e) when (e is RpcException or InvalidOperationException)
        {
            // La llamada ya había terminado.
        }
        finally
        {
            _sending.Release();
        }
    }

    public void Cancel()
    {
        _sendingDone = true;
        _life.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        if (_reading is { } reading) await reading;
        _call.Dispose();
        _life.Dispose();
        _sending.Dispose();
    }
}
