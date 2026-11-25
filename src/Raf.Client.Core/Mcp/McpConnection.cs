using Raf.Client.Core.Api;
using Raf.Client.Core.Local;
using Raf.Client.Core.Session;
using Raf.Contracts;

namespace Raf.Client.Core.Mcp;

/// <summary>El MCP tiene una sesión guardada: con qué servidor y qué usuario.</summary>
public sealed record McpSessionInfo(Uri Server, UserDto User);

/// <summary>
/// La sesión propia del MCP (Plan 12, M2): la crea la app desde Preferencias con la sesión del usuario, se guarda
/// cifrada aparte de la de la app y se revoca sin cerrar la de la app. El proceso MCP la usa con su propio
/// <see cref="SessionManager"/>, que la renueva con el mismo lock entre procesos.
/// </summary>
public sealed class McpConnection(HttpClient http, ISecureStore store, TimeProvider clock, string dataDirectory)
{
    public const string StoreKey = "mcp-session";

    private string LockPath => Path.Combine(dataDirectory, "mcp-session.lock");

    /// <summary>Un administrador de sesión sobre la del MCP, sin restaurar.</summary>
    public SessionManager CreateSession() => new(http, store, clock, LockPath, storeKey: StoreKey);

    /// <summary>La sesión guardada del MCP, o null si no hay o venció.</summary>
    public McpSessionInfo? Current()
    {
        var session = CreateSession();
        return session.TryRestoreAny() && session.User is { } user ? new McpSessionInfo(session.Server!, user) : null;
    }

    /// <summary>
    /// Pide al servidor una sesión nueva del usuario de la app y la guarda para el MCP. Si ya había una, primero la
    /// revoca.
    /// </summary>
    public async Task<McpSessionInfo> ConnectAsync(RafApiClient api, Uri server, CancellationToken ct = default)
    {
        await DisconnectAsync();
        var tokens = await api.PostAsync<TokenResponse>("auth/sessions", new { }, ct);
        var session = CreateSession();
        session.SetServer(server);
        session.Adopt(tokens);
        return new McpSessionInfo(server, tokens.User);
    }

    /// <summary>Revoca la sesión del MCP en el servidor (si se puede) y la borra de este equipo.</summary>
    public Task DisconnectAsync()
    {
        var session = CreateSession();
        session.TryRestoreAny();
        return session.LogoutAsync();
    }
}
