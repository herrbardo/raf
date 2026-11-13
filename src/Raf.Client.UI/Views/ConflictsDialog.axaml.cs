using Avalonia.Interactivity;
using Raf.Client.Services;

namespace Raf.Client.Views;

public partial class ConflictsDialog : DialogView
{
    public ConflictsDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
