using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Raf.Client.Views;

public partial class ConflictsWindow : Window
{
    public ConflictsWindow() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
