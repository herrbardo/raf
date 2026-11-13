using Avalonia.Interactivity;
using Raf.Client.Services;

namespace Raf.Client.Views;

public partial class AdminDialog : DialogView
{
    public AdminDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
