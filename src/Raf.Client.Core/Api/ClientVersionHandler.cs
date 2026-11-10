using System.Net;
using System.Text.Json;
using Raf.Contracts;

namespace Raf.Client.Core.Api;

/// <summary>
/// Manda la versión del cliente en cada llamada y avisa si el servidor la rechaza por vieja (426). El aviso
/// llega aunque la llamada sea de fondo (sincronización, renovación del token), donde el error no se muestra.
/// </summary>
public sealed class ClientVersionHandler(string clientVersion) : DelegatingHandler
{
    /// <summary>El servidor exige una versión mayor. Recibe la versión mínima; se dispara en el hilo de la llamada.</summary>
    public event Action<string>? Outdated;

    public string ClientVersion { get; } = clientVersion;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Remove(RafHeaders.ClientVersion);
        request.Headers.TryAddWithoutValidation(RafHeaders.ClientVersion, ClientVersion);
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.UpgradeRequired)
        {
            await response.Content.LoadIntoBufferAsync(ct); // el cuerpo se vuelve a leer al armar la excepción
            Outdated?.Invoke(await ReadMinVersionAsync(response) ?? "");
        }
        return response;
    }

    internal static async Task<string?> ReadMinVersionAsync(HttpResponseMessage response) =>
        ReadMinVersion(await response.Content.ReadAsStringAsync()); // del buffer: el cuerpo se lee más de una vez

    internal static string? ReadMinVersion(string problemJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(problemJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("minClientVersion", out var min) && min.ValueKind == JsonValueKind.String
                ? min.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>El servidor no acepta esta versión del cliente: hay que actualizar la app.</summary>
public sealed class ClientOutdatedException(string message, string? minVersion)
    : ApiException(HttpStatusCode.UpgradeRequired, message)
{
    public string? MinVersion { get; } = minVersion;
}
