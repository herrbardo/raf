using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Raf.Client.Core.Diagnostics;
using Raf.Contracts;
using Velopack;
using Velopack.Sources;

namespace Raf.Client.Services;

/// <summary>Versión de la app y origen de las actualizaciones, fijados al compilar.</summary>
public static class AppIdentity
{
    private static readonly Assembly Entry = typeof(AppIdentity).Assembly;

    /// <summary>Versión semántica sin metadatos de build ("1.2.0", "1.3.0-beta.1").</summary>
    public static string Version { get; } = AppVersion.Display(
        Entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary>
    /// URL del repositorio de GitHub donde se publican los releases. La pasa el workflow de release
    /// (<c>-p:RafUpdateUrl=...</c>); en un build local queda vacía y las actualizaciones se desactivan.
    /// </summary>
    public static string? UpdateUrl { get; } = Entry.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "RafUpdateUrl")?.Value is { Length: > 0 } url ? url : null;

    /// <summary>
    /// Versión de prueba ("1.3.0-beta.1"): se publica como prerelease y busca actualizaciones también entre los
    /// prereleases. Una versión estable solo se actualiza a otra estable.
    /// </summary>
    public static bool IsPrerelease => Version.Contains('-');

    public static string? ReleasesPage => UpdateUrl is null ? null
        : UpdateUrl.TrimEnd('/') + (IsPrerelease ? "/releases" : "/releases/latest");
}

/// <summary>
/// Busca versiones nuevas en GitHub Releases y las descarga en segundo plano. La versión descargada se aplica al
/// reiniciar desde el aviso o, si el usuario no lo hace, la próxima vez que se abre la app (Velopack la aplica al
/// arrancar).
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    private readonly UpdateManager? _manager;
    private readonly EventLog _events;
    private UpdateInfo? _ready;

    /// <summary>Versión descargada y lista para instalar; null si no hay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ReadyText))]
    private string? _readyVersion;

    [ObservableProperty] private bool _isChecking;

    public UpdateService(EventLog events)
    {
        _events = events;
        if (AppIdentity.UpdateUrl is null)
        {
            events.Info(EventCategory.Updates, Texts.Updates_LocalBuild);
            return;
        }
        try
        {
            var manager = new UpdateManager(new GithubSource(AppIdentity.UpdateUrl, accessToken: null, prerelease: AppIdentity.IsPrerelease));
            if (manager.IsInstalled) _manager = manager;
            else events.Info(EventCategory.Updates, Texts.Updates_NotInstalled);
        }
        catch (Exception e)
        {
            events.Warning(EventCategory.Updates, string.Format(Texts.Updates_Disabled, e.Message), e.ToString());
        }
    }

    /// <summary>La app se instaló con el instalador y sabe dónde buscar versiones nuevas.</summary>
    public bool IsSupported => _manager is not null;

    public bool IsReady => ReadyVersion is not null;

    public string ReadyText => string.Format(Texts.Updates_Ready, ReadyVersion);

    /// <summary>Busca y descarga la última versión. Devuelve true si quedó una lista para instalar.</summary>
    public async Task<bool> CheckAndDownloadAsync()
    {
        if (_manager is null) return false;
        if (_ready is not null) return true;
        await Dispatcher.UIThread.InvokeAsync(() => IsChecking = true);
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                _events.Info(EventCategory.Updates, Texts.Updates_None);
                return false;
            }
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            var version = info.TargetFullRelease.Version.ToString();
            _events.Info(EventCategory.Updates, string.Format(Texts.Updates_Downloaded, version));
            await Dispatcher.UIThread.InvokeAsync(() => ReadyVersion = version);
            return true;
        }
        catch (Exception e)
        {
            // Sin conexión o GitHub caído: se vuelve a intentar en el próximo arranque.
            _events.Warning(EventCategory.Updates, string.Format(Texts.Updates_CheckFailed, e.Message), e.ToString());
            return false;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsChecking = false);
        }
    }

    /// <summary>Cierra la app, instala la versión descargada y la vuelve a abrir.</summary>
    public void ApplyAndRestart()
    {
        if (_manager is not null && _ready is not null) _manager.ApplyUpdatesAndRestart(_ready);
    }
}
