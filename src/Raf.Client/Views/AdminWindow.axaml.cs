using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Raf.Client.Views;

public partial class AdminWindow : Window
{
    public AdminWindow() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
