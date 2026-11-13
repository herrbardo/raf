using Avalonia.Controls;
using Avalonia.Interactivity;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class EnvironmentsWindow : Window
{
    private bool _confirmedClose;

    public EnvironmentsWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_confirmedClose || DataContext is not EnvironmentsViewModel { HasChanges: true } vm) return;

        e.Cancel = true;
        if (await vm.Dialogs.ConfirmAsync(Texts.Main_UnsavedTitle, Texts.Env_CloseUnsaved, Texts.Main_Discard))
        {
            _confirmedClose = true;
            Close();
        }
    }
}
