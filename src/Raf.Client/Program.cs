using Avalonia;
using Raf.Client.Mcp;
using Raf.Client.Services;
using Velopack;

namespace Raf.Client;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // RAF.exe mcp: el servidor MCP por stdio (Plan 12), sin interfaz. Va antes que Velopack, que podría aplicar
        // una actualización pendiente y reiniciar el proceso, cortando la conversación con el harness.
        if (args is ["mcp", ..])
        {
            RafMcpServer.RunStdioAsync(new DesktopHost().Version).GetAwaiter().GetResult();
            return;
        }

        // Primero que nada: el instalador y las actualizaciones lanzan la app con argumentos propios y esperan
        // que termine enseguida. También aplica una actualización descargada que quedó sin instalar.
        VelopackApp.Build().Run();
        ClientHost.Current = new DesktopHost();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
