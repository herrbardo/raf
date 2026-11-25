using Raf.Client.Core.Api;
using Raf.Client.Core.Local;
using Raf.Client.Core.Mcp;
using Raf.Client.Core.Session;

namespace Raf.Server.Tests;

/// <summary>La sesión propia del MCP (Plan 12, etapa 1), contra el servidor real.</summary>
public sealed class McpConnectionTests(RafApp app)
{
    private static readonly Uri Server = new("http://localhost/");

    [Fact]
    public async Task Connecting_creates_a_separate_session_that_disconnects_without_closing_the_app()
    {
        var http = app.Anonymous();
        var store = new MemorySecureStore();
        var dir = Path.Combine(Path.GetTempPath(), "raf-mcp-" + Guid.NewGuid().ToString("N"));
        var appSession = new SessionManager(http, store, TimeProvider.System, Path.Combine(dir, "session.lock"));
        appSession.SetServer(Server);
        await appSession.LoginAsync(RafApp.AdminEmail, RafApp.AdminPassword);
        var api = new RafApiClient(http, appSession);
        var mcp = new McpConnection(http, store, TimeProvider.System, dir);
        Assert.Null(mcp.Current());

        var connected = await mcp.ConnectAsync(api, Server);

        Assert.Equal((Server, RafApp.AdminEmail), (connected.Server, connected.User.Email));
        Assert.Equal(connected, mcp.Current());
        Assert.NotNull(store.Read(SessionManager.AppStoreKey));
        Assert.NotEqual(store.Read(SessionManager.AppStoreKey), store.Read(McpConnection.StoreKey));

        // El proceso MCP la recupera sin saber el servidor y puede renovarla.
        var fromMcp = mcp.CreateSession();
        Assert.True(fromMcp.TryRestoreAny());
        var before = store.Read(McpConnection.StoreKey);
        await fromMcp.RefreshAfterRejectionAsync(await fromMcp.GetAccessTokenAsync());
        Assert.NotEqual(before, store.Read(McpConnection.StoreKey)); // rotó el refresh token y lo guardó

        // Desconectar desde la app revoca el token rotado por el proceso MCP, no el viejo que conoce la app.
        await mcp.DisconnectAsync();
        Assert.Null(mcp.Current());

        // Volver a conectar reemplaza la sesión; desconectar la revoca y la app sigue con la suya.
        var again = await mcp.ConnectAsync(api, Server);
        Assert.NotNull(again);
        await mcp.DisconnectAsync();
        Assert.Null(mcp.Current());
        Assert.Null(store.Read(McpConnection.StoreKey));
        var appBefore = store.Read(SessionManager.AppStoreKey);
        await appSession.RefreshAfterRejectionAsync(await appSession.GetAccessTokenAsync());
        Assert.NotEqual(appBefore, store.Read(SessionManager.AppStoreKey)); // la sesión de la app sigue viva
    }
}
