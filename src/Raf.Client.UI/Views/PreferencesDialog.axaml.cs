using Avalonia.Interactivity;
using Raf.Client.Services;

namespace Raf.Client.Views;

public partial class PreferencesDialog : DialogView
{
    public PreferencesDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
