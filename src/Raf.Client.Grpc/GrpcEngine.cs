using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Grpc.Core;
using Grpc.Net.Client;
using Raf.Client.Core.Grpc;

namespace Raf.Client.Grpc;

/// <summary>
/// <see cref="IGrpcEngine"/> con Grpc.Net.Client: un canal por dirección y por validación de certificados, los
/// descriptores de cada dirección en memoria, y las llamadas con un marshaller de bytes que sirve para cualquier método.
/// </summary>
public sealed partial class GrpcEngine(Func<bool, HttpMessageHandler>? handlers = null) : IGrpcEngine, IDisposable
{
    /// <summary>Tope de un mensaje recibido: suficiente para respuestas grandes sin que una sola agote la memoria.</summary>
    public const int MaxReceiveMessageSize = 64 * 1024 * 1024;

    private static readonly Marshaller<byte[]> Bytes = Marshallers.Create(b => b, b => b);

    private readonly ConcurrentDictionary<(string Address, bool Insecure), GrpcChannel> _channels = new();
    private readonly ConcurrentDictionary<string, DescriptorSet> _descriptors = new();

    public async Task<IReadOnlyList<GrpcMethodInfo>> GetMethodsAsync(GrpcTarget target, bool refresh = false, CancellationToken ct = default) =>
        (await DescriptorsAsync(target, refresh, ct)).Methods;

    private async Task<DescriptorSet> DescriptorsAsync(GrpcTarget target, bool refresh, CancellationToken ct)
    {
        var key = target.Address.GetLeftPart(UriPartial.Authority);
        if (!refresh && _descriptors.TryGetValue(key, out var cached)) return cached;

        var invoker = Channel(target).CreateCallInvoker();
        try
        {
            var files = await ReflectionClient.LoadAsync(invoker, () => Options(target, ct), target.Address.Authority, ct);
            var set = DescriptorSet.Build(files);
            _descriptors[key] = set;
            return set;
        }
        catch (RpcException e) when (!ct.IsCancellationRequested)
        {
            throw new GrpcEngineException(string.Format(CoreTexts.Grpc_ReflectionFailed, Describe(e.Status)), e);
        }
        catch (RpcException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public async Task<GrpcCallResult> CallUnaryAsync(GrpcCall call, CancellationToken ct = default)
    {
        var (set, method) = await FindMethodAsync(call, ct);
        if (method.IsClientStreaming || method.IsServerStreaming)
            throw new GrpcEngineException(string.Format(CoreTexts.Grpc_NotUnary, $"{call.Service}/{call.Method}"));

        var json = new ProtoJson(set.FindMessage);
        var request = json.Encode(call.MessageJson, method.InputType);
        var definition = new Method<byte[], byte[]>(MethodType.Unary, call.Service, call.Method, Bytes, Bytes);
        using var pending = Channel(call.Target).CreateCallInvoker()
            .AsyncUnaryCall(definition, null, Options(call.Target, ct, deadline: true), request);

        try
        {
            var response = await pending.ResponseAsync;
            var headers = await pending.ResponseHeadersAsync;
            var status = pending.GetStatus();
            return Result(status, headers, pending.GetTrailers(), json.Decode(response, method.OutputType));
        }
        catch (RpcException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (RpcException e)
        {
            Metadata headers;
            try
            {
                headers = await pending.ResponseHeadersAsync;
            }
            catch (RpcException)
            {
                headers = []; // no llegó ni la metadata: por ejemplo, no se pudo conectar
            }
            return Result(e.Status, headers, e.Trailers, null);
        }
    }

    private async Task<(DescriptorSet Set, Google.Protobuf.Reflection.MethodDescriptor Method)> FindMethodAsync(GrpcCall call, CancellationToken ct)
    {
        var set = await DescriptorsAsync(call.Target, refresh: false, ct);
        if (set.FindMethod(call.Service, call.Method) is { } method) return (set, method);

        // Puede que el servidor haya cambiado desde que se pidieron los descriptores.
        set = await DescriptorsAsync(call.Target, refresh: true, ct);
        return (set, set.FindMethod(call.Service, call.Method)
            ?? throw new GrpcEngineException(string.Format(CoreTexts.Grpc_UnknownMethod, $"{call.Service}/{call.Method}")));
    }

    public async Task<IGrpcStream> StartStreamAsync(GrpcCall call, CancellationToken ct = default)
    {
        var (set, method) = await FindMethodAsync(call, ct);
        if (!method.IsClientStreaming && !method.IsServerStreaming)
            throw new GrpcEngineException(string.Format(CoreTexts.Grpc_NotStreaming, $"{call.Service}/{call.Method}"));

        var json = new ProtoJson(set.FindMessage);
        var invoker = Channel(call.Target).CreateCallInvoker();
        var life = new CancellationTokenSource();

        // G4 del plan: el timeout es deadline solo en server streaming; con envío del cliente la llamada dura lo que se quiera.
        var options = Options(call.Target, life.Token, deadline: !method.IsClientStreaming);
        if (!method.IsClientStreaming)
        {
            var request = json.Encode(call.MessageJson, method.InputType);
            var definition = new Method<byte[], byte[]>(MethodType.ServerStreaming, call.Service, call.Method, Bytes, Bytes);
            return new GrpcStream(GrpcMethodKind.ServerStreaming, json, method, life,
                invoker.AsyncServerStreamingCall(definition, null, options, request), request.Length);
        }
        if (!method.IsServerStreaming)
        {
            var definition = new Method<byte[], byte[]>(MethodType.ClientStreaming, call.Service, call.Method, Bytes, Bytes);
            return new GrpcStream(GrpcMethodKind.ClientStreaming, json, method, life, invoker.AsyncClientStreamingCall(definition, null, options));
        }
        var duplex = new Method<byte[], byte[]>(MethodType.DuplexStreaming, call.Service, call.Method, Bytes, Bytes);
        return new GrpcStream(GrpcMethodKind.DuplexStreaming, json, method, life, invoker.AsyncDuplexStreamingCall(duplex, null, options));
    }

    internal static GrpcCallResult Result(Status status, Metadata headers, Metadata trailers, string? message) =>
        new((int)status.StatusCode, StatusName(status.StatusCode), Detail(status), Pairs(headers), Pairs(trailers), message);

    internal static string? Detail(Status status) =>
        status.StatusCode == StatusCode.OK ? null
        : status.DebugException is { } e ? $"{status.Detail} ({e.Message})"
        : string.IsNullOrEmpty(status.Detail) ? null : status.Detail;

    private static string Describe(Status status) => $"{StatusName(status.StatusCode)}: {Detail(status)}";

    /// <summary>Los valores binarios (claves que terminan en <c>-bin</c>) se muestran en base64.</summary>
    private static List<KeyValuePair<string, string>> Pairs(Metadata metadata) =>
        metadata.Select(e => KeyValuePair.Create(e.Key, e.IsBinary ? Convert.ToBase64String(e.ValueBytes) : e.Value)).ToList();

    /// <summary>El nombre canónico del estado: <c>NotFound</c> → <c>NOT_FOUND</c>.</summary>
    public static string StatusName(StatusCode code) =>
        code == StatusCode.OK ? "OK" : CamelBoundary().Replace(code.ToString(), "_$1").ToUpperInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex CamelBoundary();

    private static CallOptions Options(GrpcTarget target, CancellationToken ct, bool deadline = false)
    {
        var metadata = new Metadata();
        foreach (var (key, value) in target.Metadata)
        {
            try
            {
                if (key.EndsWith(Metadata.BinaryHeaderSuffix, StringComparison.Ordinal)) metadata.Add(key, Convert.FromBase64String(value));
                else metadata.Add(key, value);
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                throw new GrpcEngineException($"Metadata '{key}': {e.Message}", e);
            }
        }
        return new CallOptions(metadata, deadline ? DateTime.UtcNow + target.Timeout : null, ct);
    }

    private GrpcChannel Channel(GrpcTarget target) =>
        _channels.GetOrAdd((target.Address.GetLeftPart(UriPartial.Authority), target.AcceptInvalidCertificates), key =>
            GrpcChannel.ForAddress(key.Address, new GrpcChannelOptions
            {
                HttpHandler = handlers?.Invoke(key.Insecure) ?? CreateHandler(key.Insecure),
                MaxReceiveMessageSize = MaxReceiveMessageSize,
                MaxSendMessageSize = null,
                DisposeHttpClient = true,
            }));

    private static SocketsHttpHandler CreateHandler(bool acceptInvalidCertificates)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
        };
        if (acceptInvalidCertificates)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values) channel.Dispose();
        _channels.Clear();
    }
}
