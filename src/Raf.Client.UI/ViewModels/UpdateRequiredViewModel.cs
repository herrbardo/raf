using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Services;

namespace Raf.Client.ViewModels;

/// <summary>El servidor ya no acepta esta versión de la app: se ofrece actualizarla en lugar de seguir fallando.</summary>
public sealed partial class UpdateRequiredViewModel(
    ShellViewModel shell, IUpdateService updates, string clientVersion, string minVersion) : PageViewModel
{
    public IUpdateService Updates { get; } = updates;

    public string Message => string.IsNullOrEmpty(minVersion)
        ? string.Format(Texts.Update_Incompatible, clientVersion)
        : string.Format(Texts.Update_IncompatibleMin, clientVersion, minVersion);

    public bool CanUpdateInApp => Updates.IsSupported;

    public string? ReleasesPage => Updates.ReleasesPage;

    public bool CanOpenReleases => !CanUpdateInApp && ReleasesPage is not null;

    public string ManualHint => CanOpenReleases
        ? Texts.Update_HowInstaller
        : Texts.Update_HowAdmin;

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            if (await Updates.CheckAndDownloadAsync()) Updates.ApplyAndRestart();
            else Error = Texts.Update_NotFound;
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
            Error = string.Format(Texts.Update_BrowserFailed, ReleasesPage);
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
