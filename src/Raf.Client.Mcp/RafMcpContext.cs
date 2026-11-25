using System.Text.Json;
using ModelContextProtocol;
using Raf.Client.Core.Api;
using Raf.Client.Core.Execution;
using Raf.Client.Core.History;
using Raf.Client.Core.Local;
using Raf.Client.Core.Mcp;
using Raf.Client.Core.Session;
using Raf.Client.Core.Workspaces;
using Raf.Contracts;

namespace Raf.Client.Mcp;

/// <summary>Lo que necesita una herramienta (Plan 12, M5).</summary>
public enum McpAccess { Read, Run, Write }

/// <summary>
/// Lo que comparten las herramientas del proceso MCP: la sesión propia del MCP (Plan 12, M2), la API sin caché local
/// (M6) y los permisos, que se releen en cada llamada para que un cambio en Preferencias valga enseguida (M5).
/// </summary>
public sealed class RafMcpContext(
    McpConnection connection, HttpClient http, Func<LocalSettings> settings, ISecureStore secure,
    RunHistoryStore? history = null, RequestRunner? runner = null)
{
    /// <summary>Envía los requests desde este equipo, como la app (M1). gRPC y WebSocket, más adelante.</summary>
    internal RequestRunner Runner { get; } = runner ?? new RequestRunner();

    internal RunHistoryStore? History => history;

    /// <summary>
    /// Las variables de sesión del usuario en el servidor del MCP: las mismas que usa la app (M8). Se crea en cada
    /// llamada y no guarda nada en memoria, así que ve lo último que guardó la app.
    /// </summary>
    internal SessionVariableStore SessionVariables() => SessionVariableStore.For(secure, Session.Server!, Session.User!.Id);

    private readonly object _sync = new();
    private SessionManager? _session;
    private IWorkspaceStore? _store;

    /// <summary>Las preferencias del equipo, recién leídas.</summary>
    public LocalSettings Settings => settings();

    /// <summary>La API con la sesión del MCP, si el permiso lo deja. Si no, un error que explica qué activar.</summary>
    public IWorkspaceStore Store(McpAccess need)
    {
        var permissions = Settings.Mcp;
        var allowed = need switch
        {
            McpAccess.Run => permissions.CanRun,
            McpAccess.Write => permissions.CanWrite,
            _ => permissions.CanRead,
        };
        if (!allowed)
        {
            var what = need switch
            {
                McpAccess.Run => CoreTexts.Mcp_PermissionRun,
                McpAccess.Write => CoreTexts.Mcp_PermissionWrite,
                _ => CoreTexts.Mcp_PermissionRead,
            };
            throw new McpException(string.Format(CoreTexts.Mcp_NotAllowed, what));
        }

        lock (_sync)
        {
            if (_store is not null) return _store;
            var session = connection.CreateSession();
            if (!session.TryRestoreAny()) throw new McpException(CoreTexts.Mcp_NotConnectedError);
            _session = session;
            return _store = new ApiWorkspaceStore(new RafApiClient(http, session));
        }
    }

    /// <summary>La sesión del MCP ya restaurada; solo después de <see cref="Store"/>.</summary>
    internal SessionManager Session => _session ?? throw new InvalidOperationException();

    /// <summary>
    /// Ejecuta una herramienta y traduce los errores de la API a un error de la herramienta, con un mensaje que el
    /// asistente puede mostrar. Si la sesión venció, la próxima llamada vuelve a leer la guardada (por si se reconectó).
    /// </summary>
    public async Task<string> RunAsync(Func<Task<object>> tool)
    {
        try
        {
            return JsonSerializer.Serialize(await tool(), ClientJson.Options);
        }
        catch (SessionExpiredException)
        {
            Forget();
            throw new McpException(CoreTexts.Mcp_SessionExpired);
        }
        catch (ApiException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    private void Forget()
    {
        lock (_sync) (_session, _store) = (null, null);
    }
}
