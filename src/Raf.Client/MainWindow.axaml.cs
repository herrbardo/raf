using Avalonia.Controls;
using Raf.Client.ViewModels;

namespace Raf.Client;

/// <summary>La ventana de la app de escritorio. Su contenido es <see cref="Views.ShellView"/>, igual que la página web.</summary>
public partial class MainWindow : Window
{
    private bool _confirmedClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Activated += (_, _) => (DataContext as ShellViewModel)?.Main?.OnWindowActivated();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_confirmedClose || DataContext is not ShellViewModel { Main: { HasUnsavedChanges: true } main }) return;

        e.Cancel = true;
        if (await main.Services.Dialogs.ConfirmAsync(
                Texts.Main_UnsavedTitle, Texts.Window_ExitUnsaved, Texts.Window_Exit))
        {
            _confirmedClose = true;
            Close();
        }
    }
}
