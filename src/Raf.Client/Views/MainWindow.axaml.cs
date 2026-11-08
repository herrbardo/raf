using Avalonia.Controls;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class MainWindow : Window
{
    private bool _confirmedClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
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
