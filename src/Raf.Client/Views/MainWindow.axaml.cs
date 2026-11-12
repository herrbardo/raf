using Avalonia.Controls;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class MainWindow : Window
{
    private bool _confirmedClose;
    private EventConsoleWindow? _console;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed += (_, _) => _console?.Close();
        Activated += (_, _) => (DataContext as ShellViewModel)?.Main?.OnWindowActivated();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell) shell.ShowEventConsole = () => ShowEventConsole(shell);
        };
    }

    /// <summary>Hay una sola consola: si ya está abierta, se trae al frente.</summary>
    private void ShowEventConsole(ShellViewModel shell)
    {
        if (_console is not null)
        {
            if (_console.WindowState == WindowState.Minimized) _console.WindowState = WindowState.Normal;
            _console.Activate();
            return;
        }

        _console = new EventConsoleWindow { DataContext = shell.EventConsole };
        _console.Closed += (_, _) => _console = null;
        _console.Show(this);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_confirmedClose || DataContext is not ShellViewModel { Main: { HasUnsavedChanges: true } main }) return;

        e.Cancel = true;
        if (await main.Services.Dialogs.ConfirmAsync(
                "Cambios sin guardar", "Hay pestañas con cambios sin guardar. ¿Salir y descartarlos?", "Salir"))
        {
            _confirmedClose = true;
            Close();
        }
    }
}
