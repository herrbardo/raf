using Avalonia.Controls;
using Avalonia.Interactivity;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class AppAuthWindow : Window
{
    public AppAuthWindow() => InitializeComponent();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as AppAuthViewModel)?.Accept();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
