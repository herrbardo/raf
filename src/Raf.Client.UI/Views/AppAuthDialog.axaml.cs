using Avalonia.Interactivity;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class AppAuthDialog : DialogView
{
    public AppAuthDialog() => InitializeComponent();

    /// <summary>Si falta algo del login, el diálogo queda abierto con el motivo.</summary>
    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppAuthViewModel vm && !vm.Accept()) return;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
