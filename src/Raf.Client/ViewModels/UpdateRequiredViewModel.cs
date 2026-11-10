using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Services;

namespace Raf.Client.ViewModels;

/// <summary>El servidor ya no acepta esta versión de la app: se ofrece actualizarla en lugar de seguir fallando.</summary>
public sealed partial class UpdateRequiredViewModel(
    ShellViewModel shell, UpdateService updates, string clientVersion, string minVersion) : PageViewModel
{
    public UpdateService Updates { get; } = updates;

    public string Message => string.IsNullOrEmpty(minVersion)
        ? $"Esta versión de RAF ({clientVersion}) ya no es compatible con el servidor."
        : $"Esta versión de RAF ({clientVersion}) ya no es compatible con el servidor, que pide la {minVersion} o posterior.";

    public bool CanUpdateInApp => Updates.IsSupported;

    public string? ReleasesPage => AppIdentity.ReleasesPage;

    public bool CanOpenReleases => !CanUpdateInApp && ReleasesPage is not null;

    public string ManualHint => CanOpenReleases
        ? "Descargá e instalá la última versión. Tus datos locales se conservan."
        : "Pedile la versión nueva al administrador del servidor. Tus datos locales se conservan.";

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            if (await Updates.CheckAndDownloadAsync()) Updates.ApplyAndRestart();
            else Error = "No se encontró una versión nueva o no se pudo descargar. Revisá la conexión y probá de nuevo.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenReleases()
    {
        if (ReleasesPage is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Error = $"No se pudo abrir el navegador. La versión nueva está en {ReleasesPage}";
        }
    }

    /// <summary>Por si el administrador bajó la versión mínima.</summary>
    [RelayCommand]
    private Task RetryAsync()
    {
        shell.ShowLogin();
        return shell.StartAsync();
    }

    [RelayCommand]
    private void ChangeServer() => shell.ShowConnect();
}
