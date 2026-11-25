using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Api;
using Raf.Client.Core.Local;
using Raf.Client.Core.Mcp;
using Raf.Client.Services;

namespace Raf.Client.ViewModels;

/// <summary>
/// Preferencias del asistente por MCP (Plan 12, etapa 1): conectar o desconectar su sesión propia, qué puede hacer y
/// la configuración para copiar en el harness.
/// </summary>
public sealed partial class McpPreferencesViewModel : ObservableObject
{
    private readonly McpConnection _connection;
    private readonly RafApiClient _api;
    private readonly SettingsStore _settings;
    private readonly IDialogs _dialogs;
    private readonly Uri _server;
    private readonly bool _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(StatusText))]
    private McpSessionInfo? _session;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseMore))]
    private bool _canRead;

    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private bool _canWrite;

    public McpPreferencesViewModel(
        McpConnection connection, RafApiClient api, SettingsStore settings, IDialogs dialogs, Uri server, string executable)
    {
        _connection = connection;
        _api = api;
        _settings = settings;
        _dialogs = dialogs;
        _server = server;
        Executable = executable;

        _loading = true;
        var permissions = settings.Current.Mcp;
        CanRead = permissions.Read;
        CanRun = permissions.Run;
        CanWrite = permissions.Write;
        _loading = false;

        _session = connection.Current();
    }

    public string Executable { get; }

    public bool IsConnected => Session is not null;

    /// <summary>Ejecutar y escribir necesitan leer (M5).</summary>
    public bool CanChooseMore => CanRead;

    public string StatusText => Session is { } s
        ? string.Format(Texts.Mcp_Connected, s.User.Email, s.Server)
        : Texts.Mcp_NotConnected;

    /// <summary>El comando para Claude Code.</summary>
    public string ClaudeCodeCommand => $"claude mcp add raf -- \"{Executable}\" mcp";

    /// <summary>El bloque para <c>.vscode/mcp.json</c> (GitHub Copilot en VS Code).</summary>
    public string VsCodeConfig => JsonSerializer.Serialize(
        new { servers = new { raf = new { type = "stdio", command = Executable, args = new[] { "mcp" } } } },
        new JsonSerializerOptions { WriteIndented = true });

    /// <summary>La conexión es con el servidor y el usuario de la app; si son otros, conectar la reemplaza.</summary>
    public bool IsOtherServer => Session is { } s && s.Server != _server;

    partial void OnCanReadChanged(bool value) => SavePermissions();
    partial void OnCanRunChanged(bool value) => SavePermissions();
    partial void OnCanWriteChanged(bool value) => SavePermissions();

    private void SavePermissions()
    {
        if (_loading) return;
        _settings.Update(s => s.Mcp = new McpPermissions(CanRead, CanRun, CanWrite));
    }

    [RelayCommand]
    private Task ConnectAsync() => GuardAsync(async () =>
    {
        if (IsConnected && !await _dialogs.ConfirmAsync(Texts.Mcp_ReconnectTitle, Texts.Mcp_ReconnectMessage, Texts.Mcp_Connect))
            return;
        Session = await _connection.ConnectAsync(_api, _server);
        OnPropertyChanged(nameof(IsOtherServer));
    });

    [RelayCommand]
    private Task DisconnectAsync() => GuardAsync(async () =>
    {
        await _connection.DisconnectAsync();
        Session = null;
        OnPropertyChanged(nameof(IsOtherServer));
    });

    private async Task GuardAsync(Func<Task> action)
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
