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

/// <summary>
/// Cómo se calcula cuándo vence el token: el <c>exp</c> del JWT, un campo del body de la respuesta (segundos como
/// <c>expires_in</c>, una fecha o un instante Unix), un tiempo fijo, o nunca (se renueva solo ante un 401).
/// </summary>
public enum TokenExpiry { JwtExp, ResponseField, Fixed, None }

/// <summary>
/// Login automático de una App: antes de un request que hereda la auth de la App, si no hay token o venció, se
/// ejecuta <see cref="Request"/> y el valor de <see cref="TokenPath"/> se guarda en la variable de sesión
/// <see cref="Variable"/>, que la auth de la App usa como <c>{{nombre}}</c>. <see cref="Enabled"/> en false lo
/// apaga sin perder la configuración.
/// </summary>
public sealed record AppLogin(
    bool Enabled,
    RequestSpec Request,
    ExtractionSource TokenSource,
    string TokenPath,
    string Variable,
    TokenExpiry Expiry = TokenExpiry.JwtExp,
    string? ExpiryPath = null,
    int? ExpiryMinutes = null)
{
    public const string DefaultVariable = "token";

    public static AppLogin Default { get; } = new(
        false, RequestSpec.Empty with { Method = "POST", Body = new BodySpec(BodyType.Json, "") },
        ExtractionSource.Body, "$.access_token", DefaultVariable);

    /// <summary>
    /// Qué le falta para poder guardarse. Apagado se acepta incompleto, salvo que herede la auth: el login de la
    /// App no tiene de dónde heredarla.
    /// </summary>
    public AppLoginProblem Problem()
    {
        if (Request is not { Headers: not null, QueryParams: not null, Body: not null, Auth: not null }) return AppLoginProblem.Incomplete;
        if (Request.Auth.Type == AuthType.Inherit) return AppLoginProblem.InheritAuth;
        if (!Enabled) return AppLoginProblem.None;
        if (string.IsNullOrWhiteSpace(Request.Method) || Request.Method.Length > 20) return AppLoginProblem.InvalidMethod;
        if (string.IsNullOrWhiteSpace(Request.Url)) return AppLoginProblem.MissingUrl;
        if (string.IsNullOrWhiteSpace(TokenPath)) return AppLoginProblem.MissingTokenPath;
        if (!VariableNames.IsValid(Variable)) return AppLoginProblem.InvalidVariable;
        return Expiry switch
        {
            TokenExpiry.ResponseField when string.IsNullOrWhiteSpace(ExpiryPath) => AppLoginProblem.MissingExpiryPath,
            TokenExpiry.Fixed when ExpiryMinutes is not > 0 => AppLoginProblem.InvalidExpiryMinutes,
            _ => AppLoginProblem.None,
        };
    }
}

public enum AppLoginProblem
{
    None, Incomplete, InheritAuth, InvalidMethod, MissingUrl, MissingTokenPath, InvalidVariable, MissingExpiryPath, InvalidExpiryMinutes,
}

public static class VariableNames
{
    /// <summary>Un nombre que se puede usar como <c>{{nombre}}</c>.</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}
