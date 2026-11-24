using Google.Protobuf;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;
using Raf.Client.Core.Grpc;

namespace Raf.Client.Grpc;

/// <summary>
/// Pide los descriptores al servidor con server reflection. Prueba <c>grpc.reflection.v1</c> y, si el servidor no lo
/// tiene, <c>v1alpha</c>: los mensajes de las dos versiones son iguales en binario, así que se usan los de v1alpha.
/// </summary>
internal static class ReflectionClient
{
    private static readonly Marshaller<ServerReflectionRequest> RequestMarshaller =
        Marshallers.Create(m => m.ToByteArray(), ServerReflectionRequest.Parser.ParseFrom);

    private static readonly Marshaller<ServerReflectionResponse> ResponseMarshaller =
        Marshallers.Create(m => m.ToByteArray(), ServerReflectionResponse.Parser.ParseFrom);

    private static Method<ServerReflectionRequest, ServerReflectionResponse> Info(string service) =>
        new(MethodType.DuplexStreaming, service, "ServerReflectionInfo", RequestMarshaller, ResponseMarshaller);

    /// <summary>Todos los archivos que hacen falta para describir los servicios del servidor.</summary>
    public static async Task<List<ByteString>> LoadAsync(CallInvoker invoker, Func<CallOptions> options, string host, CancellationToken ct)
    {
        try
        {
            return await LoadAsync(invoker, Info("grpc.reflection.v1.ServerReflection"), options(), host, ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Unimplemented)
        {
            try
            {
                return await LoadAsync(invoker, Info("grpc.reflection.v1alpha.ServerReflection"), options(), host, ct);
            }
            catch (RpcException inner) when (inner.StatusCode == StatusCode.Unimplemented)
            {
                throw new GrpcEngineException(CoreTexts.Grpc_NoReflection, inner);
            }
        }
    }

    private static async Task<List<ByteString>> LoadAsync(
        CallInvoker invoker, Method<ServerReflectionRequest, ServerReflectionResponse> method, CallOptions options,
        string host, CancellationToken ct)
    {
        using var call = invoker.AsyncDuplexStreamingCall(method, null, options);

        async Task<ServerReflectionResponse> AskAsync(ServerReflectionRequest request)
        {
            request.Host = host;
            await call.RequestStream.WriteAsync(request, ct);
            if (!await call.ResponseStream.MoveNext(ct)) throw new RpcException(new Status(StatusCode.Unavailable, "reflection cerró la conexión"));
            return call.ResponseStream.Current;
        }

        var services = await AskAsync(new ServerReflectionRequest { ListServices = "" });
        if (services.MessageResponseCase == ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse)
            throw new GrpcEngineException(string.Format(CoreTexts.Grpc_ReflectionFailed, services.ErrorResponse.ErrorMessage));

        var files = new Dictionary<string, ByteString>();
        void Keep(ServerReflectionResponse response)
        {
            if (response.MessageResponseCase != ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse) return;
            foreach (var data in response.FileDescriptorResponse.FileDescriptorProto)
                files.TryAdd(Google.Protobuf.Reflection.FileDescriptorProto.Parser.ParseFrom(data).Name, data);
        }

        foreach (var service in services.ListServicesResponse.Service)
        {
            if (service.Name.StartsWith("grpc.reflection.", StringComparison.Ordinal)) continue;
            Keep(await AskAsync(new ServerReflectionRequest { FileContainingSymbol = service.Name }));
        }

        // Algunos servidores mandan solo el archivo del servicio: se piden los importados que falten.
        for (var round = 0; round < 20; round++)
        {
            var missing = DescriptorSet.MissingDependencies(files.Values).ToList();
            if (missing.Count == 0) break;
            foreach (var name in missing)
            {
                var response = await AskAsync(new ServerReflectionRequest { FileByFilename = name });
                if (response.MessageResponseCase == ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse)
                    throw new GrpcEngineException(string.Format(CoreTexts.Grpc_UnknownType, name));
                Keep(response);
            }
        }

        // Se cierra el envío y se espera el fin de la respuesta, para que el servidor no lo vea como una cancelación.
        await call.RequestStream.CompleteAsync();
        while (await call.ResponseStream.MoveNext(ct))
        {
        }
        return files.Values.ToList();
    }
}
