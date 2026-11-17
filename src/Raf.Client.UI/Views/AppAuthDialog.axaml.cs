using Avalonia.Interactivity;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class AppAuthDialog : DialogView
{
    public AppAuthDialog() => InitializeComponent();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as AppAuthViewModel)?.Accept();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
