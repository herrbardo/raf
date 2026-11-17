using Avalonia.Interactivity;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class WorkspaceSettingsDialog : DialogView
{
    public WorkspaceSettingsDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WorkspaceSettingsViewModel vm) vm.CloseRequested += () => Close();
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
