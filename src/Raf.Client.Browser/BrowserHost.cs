using System.ComponentModel;
using System.Runtime.InteropServices.JavaScript;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Raf.Client.Core.Api;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Local;
using Raf.Client.Services;
using Raf.Client.ViewModels;
using Raf.Client.Views;

namespace Raf.Client.Browser;

/// <summary>
/// El cliente web (decisiones W1 a W5 del plan 09): preferencias, sesión y variables de sesión en
/// <c>localStorage</c> (la sesión sobrevive a cerrar el navegador, Ticket-038), caché en memoria sin cifrar,
/// diálogos superpuestos y los requests por el relay del servidor.
/// </summary>
public sealed class BrowserHost : ClientHost
{
    public override bool EncryptCache => false;

    public override bool SupportsWebSockets => false;

    public override Func<Task<IDisposable>>? SessionLock => () => WebLock.AcquireAsync("session");

    public override SettingsStore CreateSettings() => new(new BrowserStorage(BrowserStorage.Local), "settings");

    public override void AddServices(IServiceCollection services)
    {
        services.AddSingleton<ISecureStore>(new BrowserStorage(BrowserStorage.Local));
        services.AddSingleton<IUpdateService, ReloadUpdateService>();
        services.AddSingleton<IWindowService, OverlayWindowService>();
        services.AddSingleton(sp => new RequestRunner(
            handlers: invalid => new RelayHandler(sp.GetRequiredService<RafApiClient>(), invalid)));
    }

    public override void Show(IApplicationLifetime? lifetime, ShellViewModel shell, IServiceProvider services)
    {
        if (lifetime is ISingleViewApplicationLifetime single) single.MainView = new ShellView { DataContext = shell };
        BrowserInterop.Shell = shell;
    }
}

/// <summary>
/// <c>localStorage</c> o <c>sessionStorage</c>, con las funciones que registra <c>main.js</c>. Lo que se guarda es
/// accesible ante un XSS. Desde el Ticket-038 la sesión va en <c>localStorage</c> para no perderla al cerrar el
/// navegador: las pestañas la comparten y renuevan los tokens de a una (<see cref="WebLock"/>).
/// </summary>
public sealed partial class BrowserStorage(string area) : ISecureStore
{
    public const string Local = "local";
    public const string Session = "session";

    public string? Read(string name) => Get(area, name);

    public void Write(string name, string value) => Set(area, name, value);

    public void Delete(string name) => Remove(area, name);

    [JSImport("storage.get", "raf")]
    private static partial string? Get(string area, string key);

    [JSImport("storage.set", "raf")]
    private static partial void Set(string area, string key, string value);

    [JSImport("storage.remove", "raf")]
    private static partial void Remove(string area, string key);
}

/// <summary>
/// Lock entre pestañas con la Web Locks API (<c>navigator.locks</c>), registrado en <c>main.js</c>. Las pestañas
/// comparten el refresh token: si dos lo renovaran a la vez, el servidor tomaría la segunda como robo.
/// </summary>
public static partial class WebLock
{
    public static async Task<IDisposable> AcquireAsync(string name) => new Held(await Acquire(name));

    [JSImport("locks.acquire", "raf")]
    private static partial Task<string> Acquire(string name);

    [JSImport("locks.release", "raf")]
    private static partial void Release(string id);

    private sealed class Held(string id) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Release(id);
        }
    }
}

/// <summary>
/// En web no hay nada que descargar: la versión nueva está publicada y alcanza con recargar la página. Lo usa la
/// pantalla de "versión no compatible" cuando el servidor responde 426.
/// </summary>
public sealed partial class ReloadUpdateService : IUpdateService
{
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public bool IsSupported => true;
    public bool IsReady => false;
    public string ReadyText => "";
    public string? ReleasesPage => null;

    public Task<bool> CheckAndDownloadAsync() => Task.FromResult(true);

    public void ApplyAndRestart() => Reload();

    [JSImport("page.reload", "raf")]
    private static partial void Reload();
}

/// <summary>Lo que <c>main.js</c> le pregunta a la app.</summary>
public static partial class BrowserInterop
{
    internal static ShellViewModel? Shell { get; set; }

    /// <summary>
    /// Al cerrar o recargar la pestaña: hay pestañas sin guardar o cambios que todavía no llegaron al servidor. La
    /// caché vive en memoria, así que se perderían.
    /// </summary>
    [JSExport]
    public static bool ShouldWarnOnLeave() =>
        Shell?.Main is { } main && (main.HasUnsavedChanges || main.SyncStatus.Pending > 0);
}
