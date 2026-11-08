namespace Raf.Domain.Requests;

/// <summary>Par clave/valor usado en headers, query params y formularios.</summary>
public sealed record KeyValueItem(string Key, string Value, bool Enabled = true);

public enum BodyType { None, Json, Text, Xml, FormUrlEncoded }

public sealed record BodySpec(BodyType Type, string? Content = null, IReadOnlyList<KeyValueItem>? Form = null)
{
    public static BodySpec None { get; } = new(BodyType.None);
}

public enum AuthType { None, Bearer, Basic, ApiKey }

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
}

/// <summary>Todo lo necesario para ejecutar una llamada HTTP.</summary>
public sealed record RequestSpec(
    string Method,
    string Url,
    IReadOnlyList<KeyValueItem> Headers,
    IReadOnlyList<KeyValueItem> QueryParams,
    BodySpec Body,
    AuthSpec Auth)
{
    public static RequestSpec Empty { get; } = new("GET", "", [], [], BodySpec.None, AuthSpec.None);
}
