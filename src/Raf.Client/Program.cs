using Avalonia;
using Velopack;

namespace Raf.Client;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Primero que nada: el instalador y las actualizaciones lanzan la app con argumentos propios y esperan
        // que termine enseguida. También aplica una actualización descargada que quedó sin instalar.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
