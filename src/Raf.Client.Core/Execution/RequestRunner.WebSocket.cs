using System.Text;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>El handshake de un WebSocket (Ticket-036): la sesión la maneja <see cref="WebSockets.WebSocketSession"/>.</summary>
public sealed partial class RequestRunner
{
    /// <summary>
    /// La URL y los headers del handshake, a partir del spec ya resuelto: la query y la API key en la query van en
    /// la URL; Bearer, Basic y la API key en header, como headers. Se acepta http(s):// y se pasa a ws(s)://.
    /// </summary>
    public static (Uri Uri, IReadOnlyList<KeyValuePair<string, string>> Headers) WebSocketHandshake(RequestSpec spec)
    {
        var query = spec.QueryParams.ToList();
        if (spec.Auth is { Type: AuthType.ApiKey, ApiKeyIn: ApiKeyLocation.Query, ApiKeyName: { Length: > 0 } queryName })
            query.Add(new KeyValueItem(queryName, spec.Auth.ApiKeyValue ?? ""));

        var text = AppendQuery(spec.Url.Trim(), query);
        var scheme = text.IndexOf("://", StringComparison.Ordinal) is > 0 and var i ? text[..i].ToLowerInvariant() : "";
        var mapped = scheme switch
        {
            "ws" or "http" => "ws",
            "wss" or "https" => "wss",
            _ => null,
        };
        if (mapped is null || !Uri.TryCreate(mapped + text[scheme.Length..], UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            throw new RequestBuildException(CoreTexts.Ws_InvalidUrl);

        var headers = spec.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h.Key))
            .Select(h => KeyValuePair.Create(h.Key.Trim(), h.Value))
            .ToList();

        void Set(string name, string value)
        {
            headers.RemoveAll(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            headers.Add(KeyValuePair.Create(name, value));
        }

        switch (spec.Auth)
        {
            case { Type: AuthType.Bearer } bearer:
                Set("Authorization", $"Bearer {bearer.Token}");
                break;
            case { Type: AuthType.Basic } basic:
                Set("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.Username}:{basic.Password}")));
                break;
            case { Type: AuthType.ApiKey, ApiKeyIn: ApiKeyLocation.Header, ApiKeyName: { Length: > 0 } name } key:
                Set(name.Trim(), key.ApiKeyValue ?? "");
                break;
        }
        return (uri, headers);
    }
}
