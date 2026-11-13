namespace Raf.Domain.Requests;

/// <summary>Par clave/valor usado en headers, query params y formularios.</summary>
public sealed record KeyValueItem(string Key, string Value, bool Enabled = true);

public enum BodyType { None, Json, Text, Xml, FormUrlEncoded }

public sealed record BodySpec(BodyType Type, string? Content = null, IReadOnlyList<KeyValueItem>? Form = null)
{
    public static BodySpec None { get; } = new(BodyType.None);
}

/// <summary><see cref="Inherit"/>: el request usa la autenticación de su App. Una App no puede tenerlo.</summary>
public enum AuthType { None, Bearer, Basic, ApiKey, Inherit }

public enum ApiKeyLocation { Header, Query }

public sealed record AuthSpec(
    AuthType Type,
    string? Token = null,
    string? Username = null,
    string? Password = null,
    string? ApiKeyName = null,
    string? ApiKeyValue = null,
    ApiKeyLocation ApiKeyIn = ApiKeyLocation.Header)
{
    public static AuthSpec None { get; } = new(AuthType.None);
    public static AuthSpec Inherit { get; } = new(AuthType.Inherit);
}

public enum ExtractionSource { Body, Header }

/// <summary>
/// Después de una respuesta 2xx, guarda en la variable de sesión <see cref="Variable"/> el valor que está en
/// <see cref="Path"/>: un JSON path o XPath si el origen es el body, o el nombre si es un header.
/// </summary>
public sealed record ResponseExtraction(string Variable, ExtractionSource Source, string Path, bool Enabled = true);

/// <summary>Todo lo necesario para ejecutar una llamada HTTP.</summary>
public sealed record RequestSpec(
    string Method,
    string Url,
    IReadOnlyList<KeyValueItem> Headers,
    IReadOnlyList<KeyValueItem> QueryParams,
    BodySpec Body,
    AuthSpec Auth,
    IReadOnlyList<ResponseExtraction>? Extractions = null)
{
    public static RequestSpec Empty { get; } = new("GET", "", [], [], BodySpec.None, AuthSpec.None);
}
