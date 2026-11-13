using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raf.Client.Core.Api;

public static class ClientJson
{
    /// <summary>Mismo formato que la API: camelCase, enums como texto y sin nulls.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Error de la API o de conexión. <see cref="Exception.Message"/> se puede mostrar al usuario.</summary>
public class ApiException(HttpStatusCode? status, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>null si no hubo respuesta del servidor.</summary>
    public HttpStatusCode? Status { get; } = status;

    internal static async Task<ApiException> FromResponseAsync(HttpResponseMessage response)
    {
        string? title = null, detail = null, minClientVersion = null;
        var errors = new List<string>();
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            minClientVersion = ClientVersionHandler.ReadMinVersion(text);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t)) title = t.GetString();
            if (root.TryGetProperty("detail", out var d)) detail = d.GetString();
            if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                foreach (var field in e.EnumerateObject())
                    errors.AddRange(field.Value.EnumerateArray().Select(x => x.GetString() ?? ""));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or HttpRequestException)
        {
            // El cuerpo no es un ProblemDetails (por ejemplo, un error del balanceador).
        }

        var message = errors.Count > 0 ? string.Join(Environment.NewLine, errors)
            : detail ?? title ?? string.Format(CoreTexts.Api_ServerAnswered, (int)response.StatusCode);

        if (response.StatusCode == HttpStatusCode.Conflict && title == "Conflicto de versión")
            return new VersionConflictException(message);
        if (response.StatusCode == HttpStatusCode.UpgradeRequired)
            return new ClientOutdatedException(message, minClientVersion);
        return new ApiException(response.StatusCode, message);
    }
}

/// <summary>El elemento cambió en el servidor desde que se cargó.</summary>
public sealed class VersionConflictException(string message) : ApiException(HttpStatusCode.Conflict, message);

/// <summary>La sesión ya no es válida: hay que volver a iniciarla.</summary>
public sealed class SessionExpiredException()
    : ApiException(HttpStatusCode.Unauthorized, CoreTexts.Api_SessionExpired);
