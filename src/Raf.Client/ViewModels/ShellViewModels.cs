using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Api;
using Raf.Client.Core.Session;
using Raf.Client.Services;

namespace Raf.Client.ViewModels;

/// <summary>Pantalla con una operación en curso y un mensaje de error.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    protected async Task GuardAsync(Func<Task> action)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Decide qué pantalla se muestra según el servidor y la sesión.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ClientServices _services;

    [ObservableProperty] private object? _page;

    public ShellViewModel(ClientServices services)
    {
        _services = services;
        services.Session.SessionExpired += () => Dispatcher.UIThread.Post(() =>
        {
            if (Page is not LoginViewModel) ShowLogin("La sesión expiró. Volvé a iniciar sesión.");
        });
    }

    public MainViewModel? Main => Page as MainViewModel;

    public async Task StartAsync()
    {
        var server = ServerUrl.Normalize(_services.Settings.Current.ServerUrl);
        if (server is null)
        {
            ShowConnect();
            return;
        }

        _services.Session.SetServer(server);
        if (!_services.Session.TryRestore())
        {
            ShowLogin();
            return;
        }

        if (_services.Session.User is { MustChangePassword: true }) ShowForcedPasswordChange();
        else await EnterMainAsync();
    }

    public void ShowConnect() => Page = new ConnectViewModel(_services, this);

    public void ShowLogin(string? message = null) => Page = new LoginViewModel(_services, this) { Error = message };

    public void ShowForcedPasswordChange() => Page = new ChangePasswordViewModel(_services, this, returnTo: null);

    public void ShowPasswordChange(MainViewModel returnTo) => Page = new ChangePasswordViewModel(_services, this, returnTo);

    public void Show(object page) => Page = page;

    public async Task EnterMainAsync()
    {
        var main = new MainViewModel(_services, this);
        Page = main;
        await main.InitializeAsync();
    }

    partial void OnPageChanged(object? value) => OnPropertyChanged(nameof(Main));
}

public sealed partial class ConnectViewModel(ClientServices services, ShellViewModel shell) : PageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InsecureWarning))]
    private string? _url = services.Settings.Current.ServerUrl;

    public bool InsecureWarning =>
        ServerUrl.Normalize(Url) is { Scheme: "http", IsLoopback: false };

    [RelayCommand]
    private Task ConnectAsync() => GuardAsync(async () =>
    {
        var server = ServerUrl.Normalize(Url);
        if (server is null)
        {
            Error = "Ingresá una URL http o https válida, incluido el subpath si lo tiene.";
            return;
        }

        await services.Api.CheckServerAsync(server);
        services.Session.SetServer(server);
        services.Settings.Update(s => s.ServerUrl = server.ToString());
        shell.ShowLogin();
    });
}

public sealed partial class LoginViewModel(ClientServices services, ShellViewModel shell) : PageViewModel
{
    [ObservableProperty] private string? _email = services.Settings.Current.LastEmail;
    [ObservableProperty] private string? _password;

    public string? ServerText => services.Session.Server?.ToString();

    [RelayCommand]
    private Task LoginAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        {
            Error = "Completá el email y la contraseña.";
            return;
        }

        var user = await services.Session.LoginAsync(Email.Trim(), Password);
        services.Settings.Update(s => s.LastEmail = user.Email);
        Password = null;

        if (user.MustChangePassword) shell.ShowForcedPasswordChange();
        else await shell.EnterMainAsync();
    });

    [RelayCommand]
    private void ChangeServer() => shell.ShowConnect();
}

public sealed partial class ChangePasswordViewModel(
    ClientServices services, ShellViewModel shell, MainViewModel? returnTo) : PageViewModel
{
    [ObservableProperty] private string? _current;
    [ObservableProperty] private string? _next;
    [ObservableProperty] private string? _repeat;

    public bool IsForced => returnTo is null;

    public string Heading => IsForced ? "Elegí una contraseña nueva" : "Cambiar contraseña";

    public string Intro => IsForced
        ? "Tu contraseña es temporal: hay que cambiarla antes de continuar."
        : "Al cambiarla se cierran tus sesiones en otros equipos.";

    [RelayCommand]
    private Task SaveAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(Current) || string.IsNullOrEmpty(Next))
        {
            Error = "Completá todos los campos.";
            return;
        }
        if (Next != Repeat)
        {
            Error = "Las contraseñas nuevas no coinciden.";
            return;
        }

        await services.Api.ChangePasswordAsync(Current, Next);
        if (returnTo is not null) shell.Show(returnTo);
        else await shell.EnterMainAsync();
    });

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (returnTo is not null)
        {
            shell.Show(returnTo);
            return;
        }
        await services.Session.LogoutAsync();
        shell.ShowLogin();
    }
}
