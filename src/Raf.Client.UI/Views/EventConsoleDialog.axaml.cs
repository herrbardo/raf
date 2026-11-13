using Avalonia.Controls;
using Avalonia.Input;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

/// <summary>No modal en desktop: se puede dejar abierta al lado de la principal mientras se usa la app.</summary>
public partial class EventConsoleDialog : DialogView
{
    public EventConsoleDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
                vm.CopyToClipboard = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        };
    }

    private EventConsoleViewModel? ViewModel => DataContext as EventConsoleViewModel;

    public override void OnShown() => ViewModel?.SetOpen(true);

    public override void OnHidden() => ViewModel?.SetOpen(false);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e is not { Key: Key.Escape, Handled: false }) return;
        e.Handled = true;
        Close();
    }
}
