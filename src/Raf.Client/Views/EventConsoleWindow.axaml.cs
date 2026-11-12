using Avalonia.Controls;
using Avalonia.Input;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

/// <summary>Ventana no modal: se puede dejar abierta al lado de la principal mientras se usa la app.</summary>
public partial class EventConsoleWindow : Window
{
    public EventConsoleWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ViewModel?.SetOpen(true);
        Closed += (_, _) => ViewModel?.SetOpen(false);
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm) vm.CopyToClipboard = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        };
    }

    private EventConsoleViewModel? ViewModel => DataContext as EventConsoleViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e is { Key: Key.Escape, Handled: false }) Close();
    }
}
