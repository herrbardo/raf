using Avalonia.Interactivity;
using Raf.Client.Services;

namespace Raf.Client.Views;

public partial class AboutDialog : DialogView
{
    public AboutDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
