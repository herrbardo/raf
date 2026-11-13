using Avalonia.Controls;
using Avalonia.Interactivity;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class AppAuthWindow : Window
{
    public AppAuthWindow() => InitializeComponent();

    /// <summary>Si falta algo del login, la ventana queda abierta con el motivo.</summary>
    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppAuthViewModel vm && !vm.Accept()) return;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
