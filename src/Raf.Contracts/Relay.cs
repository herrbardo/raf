namespace Raf.Contracts;

/// <summary>
/// Un salto HTTP que el servidor envía en nombre del cliente web (<c>POST /relay</c>): el navegador no puede llamar a
/// cualquier API por CORS. No sigue redirecciones ni usa cookies; el cliente las maneja como en desktop. El body va
/// en base64.
/// </summary>
public sealed record RelayRequest(
    string Method,
    string Url,
    IReadOnlyList<RelayHeader> Headers,
    string? Body,
    int TimeoutSeconds,
    bool AcceptInvalidCertificates = false);

public sealed record RelayHeader(string Name, string Value);

/// <summary>
/// La respuesta del destino. <see cref="Body"/> (base64) llega hasta el tope del servidor: si se cortó,
/// <see cref="Truncated"/> es true y <see cref="BodyLength"/> es el largo que declaró el destino, si lo hizo.
/// </summary>
public sealed record RelayResponse(
    int StatusCode,
    string? ReasonPhrase,
    string Version,
    IReadOnlyList<RelayHeader> Headers,
    string Body,
    long BodyLength,
    bool Truncated,
    RelayConnection? Connection);

/// <summary>La conexión nueva que abrió el servidor para el salto; null si reusó una abierta. Tiempos en milisegundos.</summary>
public sealed record RelayConnection(
    string Host,
    int Port,
    IReadOnlyList<string> Addresses,
    string? Remote,
    double DnsMs,
    double ConnectMs,
    double? TlsMs,
    string? TlsProtocol,
    string? Cipher,
    string? CertificateSubject,
    string? CertificateIssuer,
    DateTime? CertificateExpires);

/// <summary>Motivo de un relay fallido: viaja en la extensión <see cref="Extension"/> del problem+json.</summary>
public static class RelayErrors
{
    public const string Extension = "relayError";

    /// <summary>El request no se puede enviar: URL, método o body inválidos.</summary>
    public const string Invalid = "invalid";

    /// <summary>El destino es una dirección interna (localhost, red privada) y el servidor no lo permite.</summary>
    public const string Blocked = "blocked";

    /// <summary>No se pudo conectar o la conexión se cortó: DNS, TCP o TLS.</summary>
    public const string Unreachable = "unreachable";

    public const string Timeout = "timeout";

    /// <summary>El body del request supera el tope del servidor.</summary>
    public const string TooLarge = "tooLarge";

    /// <summary>El servidor tiene el relay apagado.</summary>
    public const string Disabled = "disabled";
}
