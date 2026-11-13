using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Local;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client;

/// <summary>
/// La app de escritorio: datos en la carpeta local, secretos con DPAPI/Keychain, actualizaciones con Velopack,
/// ventanas del sistema y los requests directo a la red.
/// </summary>
public sealed class DesktopHost : ClientHost
{
    public override SettingsStore CreateSettings() =>
        new(Path.Combine(AppPaths.DataDirectory, "settings.json"));

    public override void AddServices(IServiceCollection services)
    {
        services.AddSingleton(SecureStores.CreateDefault());
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton(new RequestRunner());
    }

    public override void Show(IApplicationLifetime? lifetime, ShellViewModel shell, IServiceProvider services)
    {
        if (lifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.MainWindow = new MainWindow { DataContext = shell };
        desktop.ShutdownRequested += (_, _) => (services as IDisposable)?.Dispose();
        _ = Task.Run(services.GetRequiredService<IUpdateService>().CheckAndDownloadAsync);
    }
}
