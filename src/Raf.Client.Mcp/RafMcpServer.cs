using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Raf.Client.Core.Api;
using Raf.Client.Core.Execution;
using Raf.Client.Core.History;
using Raf.Client.Core.Local;
using Raf.Client.Core.Mcp;

namespace Raf.Client.Mcp;

/// <summary>El servidor MCP de RAF (Plan 12): <c>RAF.exe mcp</c> lo atiende por stdio (M13).</summary>
public static class RafMcpServer
{
    /// <summary>Las opciones del servidor con todas las herramientas de <paramref name="context"/>.</summary>
    public static McpServerOptions CreateOptions(RafMcpContext context, string version)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "raf", Version = version },
            ServerInstructions = CoreTexts.Mcp_Instructions,
            ToolCollection = [],
        };
        foreach (var tool in Tools(new RafReadTools(context)).Concat(Tools(new RafRunTools(context))).Concat(Tools(new RafWriteTools(context))))
            options.ToolCollection.Add(tool);
        return options;
    }

    /// <summary>Cada método con <see cref="McpServerToolAttribute"/>, ligado a su instancia.</summary>
    private static IEnumerable<McpServerTool> Tools(object target) =>
        target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, target));

    /// <summary>
    /// Atiende el MCP por stdio hasta que el harness cierre la entrada. Usa la sesión del MCP y las preferencias de
    /// este equipo, en la misma carpeta de datos que la app.
    /// </summary>
    public static async Task RunStdioAsync(string version, CancellationToken ct = default)
    {
        using var http = new HttpClient(new ClientVersionHandler(version) { InnerHandler = new HttpClientHandler() })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        var data = AppPaths.DataDirectory;
        var settingsPath = Path.Combine(data, "settings.json");
        var secure = SecureStores.CreateDefault();
        var connection = new McpConnection(http, secure, TimeProvider.System, data);
        using var runner = new RequestRunner();
        var context = new RafMcpContext(connection, http, () => new SettingsStore(settingsPath).Current, secure,
            new RunHistoryStore(Path.Combine(data, "history")), runner);

        var options = CreateOptions(context, version);
        await using var transport = new StdioServerTransport(options);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(ct);
    }
}
