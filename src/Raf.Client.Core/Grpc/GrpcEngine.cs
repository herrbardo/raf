namespace Raf.Client.Core.Grpc;

/// <summary>Los cuatro tipos de llamada gRPC.</summary>
public enum GrpcMethodKind { Unary, ServerStreaming, ClientStreaming, DuplexStreaming }

/// <summary>
/// Un método que ofrece el servidor. <see cref="InputTemplate"/> es un mensaje de entrada de ejemplo en JSON, con
/// todos los campos en su valor por defecto, para empezar a escribir.
/// </summary>
public sealed record GrpcMethodInfo(
    string Service, string Method, GrpcMethodKind Kind, string InputType, string OutputType, string InputTemplate)
{
    public string FullName => $"{Service}/{Method}";
}

/// <summary>Dónde y cómo conectarse. La metadata ya tiene las variables resueltas y la autenticación aplicada.</summary>
public sealed record GrpcTarget(
    Uri Address, bool AcceptInvalidCertificates, IReadOnlyList<KeyValuePair<string, string>> Metadata, TimeSpan Timeout);

/// <summary>Una llamada unaria: el mensaje va y vuelve en JSON.</summary>
public sealed record GrpcCall(GrpcTarget Target, string Service, string Method, string MessageJson);

/// <summary>
/// Cómo terminó la llamada. <see cref="StatusCode"/> es el código gRPC (0 = OK) y <see cref="StatusName"/> su
/// nombre (<c>OK</c>, <c>NOT_FOUND</c>...). <see cref="MessageJson"/> es null si el servidor no devolvió mensaje.
/// </summary>
public sealed record GrpcCallResult(
    int StatusCode,
    string StatusName,
    string? StatusDetail,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    IReadOnlyList<KeyValuePair<string, string>> Trailers,
    string? MessageJson)
{
    public bool IsOk => StatusCode == 0;
}

/// <summary>
/// Un error antes de poder llamar o al leer la respuesta: el servidor no tiene reflection, el método no existe, el
/// mensaje no es JSON válido para el tipo. El mensaje se muestra al usuario.
/// </summary>
public sealed class GrpcEngineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Habla gRPC: descubre los servicios con server reflection y hace las llamadas traduciendo JSON a protobuf y al
/// revés. Solo existe en la app de escritorio; en web es null y el request gRPC no se puede enviar.
/// </summary>
public interface IGrpcEngine
{
    /// <summary>Los métodos que ofrece el servidor, por reflection. Se recuerdan por dirección; <paramref name="refresh"/> los vuelve a pedir.</summary>
    Task<IReadOnlyList<GrpcMethodInfo>> GetMethodsAsync(GrpcTarget target, bool refresh = false, CancellationToken ct = default);

    /// <summary>
    /// Hace una llamada unaria. Un estado distinto de OK no es una excepción: vuelve en el resultado.
    /// Lanza <see cref="GrpcEngineException"/> si no se pudo armar la llamada.
    /// </summary>
    Task<GrpcCallResult> CallUnaryAsync(GrpcCall call, CancellationToken ct = default);
}
