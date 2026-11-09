using Avalonia.Controls;
using Avalonia.Interactivity;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class WorkspaceSettingsWindow : Window
{
    public WorkspaceSettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WorkspaceSettingsViewModel vm) vm.CloseRequested += Close;
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
