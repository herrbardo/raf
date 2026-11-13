using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Raf.Client.Core.Api;
using Raf.Client.Core.Diagnostics;
using Raf.Client.Core.Execution;
using Raf.Client.Core.History;
using Raf.Client.Core.Local;
using Raf.Client.Core.Session;
using Raf.Client.Core.Workspaces;
using Raf.Client.Services;
using Raf.Client.ViewModels;
using Raf.Client.Views;

namespace Raf.Client;

public partial class App : Application
{
    private SettingsStore? _settings;

    public override void Initialize()
    {
        // El idioma va primero: los textos de la interfaz se leen al crearla y no cambian hasta reiniciar.
        _settings = new SettingsStore(Path.Combine(AppPaths.DataDirectory, "settings.json"));
        Languages.Apply(_settings.Current.Language);
        AvaloniaXamlLoader.Load(this);
        // Por defecto, oscuro (D7). Para revisar el diseño: RAF_THEME=light, o RAF_THEME=system para seguir al sistema.
        RequestedThemeVariant = Environment.GetEnvironmentVariable("RAF_THEME")?.ToLowerInvariant() switch
        {
            "dark" => ThemeVariant.Dark,
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => RequestedThemeVariant,
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var provider = BuildServices(_settings!);
            WatchUnhandledErrors(provider.GetRequiredService<EventLog>());
            var shell = provider.GetRequiredService<ShellViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownRequested += (_, _) => provider.Dispose();
            _ = shell.StartAsync();
            _ = Task.Run(provider.GetRequiredService<UpdateService>().CheckAndDownloadAsync);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Los errores que nadie atrapó también van a la consola de eventos.</summary>
    private static void WatchUnhandledErrors(EventLog events)
    {
        events.Info(EventCategory.App, string.Format(Texts.App_Started, AppIdentity.Version));
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            events.Error(EventCategory.App, string.Format(Texts.App_UnhandledUi, e.Exception.Message), e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            events.Error(EventCategory.App, string.Format(Texts.App_UnhandledBackground, e.Exception.GetBaseException().Message), e.Exception);
    }

    private static ServiceProvider BuildServices(SettingsStore settings)
    {
        var data = AppPaths.DataDirectory;
        var services = new ServiceCollection();
        services.AddSingleton(new EventLog());
        var version = new ClientVersionHandler(AppIdentity.Version) { InnerHandler = new HttpClientHandler() };
        services.AddSingleton(version);
        services.AddSingleton(new HttpClient(version) { Timeout = TimeSpan.FromSeconds(60) });
        services.AddSingleton<UpdateService>();
        services.AddSingleton(SecureStores.CreateDefault());
        services.AddSingleton(settings);
        services.AddSingleton(new RunHistoryStore(Path.Combine(data, "history")));
        services.AddSingleton(sp => new SessionManager(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ISecureStore>(),
            TimeProvider.System, Path.Combine(data, "session.lock"), sp.GetRequiredService<EventLog>()));
        services.AddSingleton<RafApiClient>();
        services.AddSingleton<IWorkspaceStore, ApiWorkspaceStore>();
        services.AddSingleton<RequestRunner>();
        services.AddSingleton<IDialogs, WindowDialogs>();
        services.AddSingleton<ClientServices>();
        services.AddSingleton<ShellViewModel>();
        return services.BuildServiceProvider();
    }
}
