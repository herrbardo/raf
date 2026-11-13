using Avalonia.Controls;
using Avalonia.Input;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            var windows = vm.Services.Windows;
            vm.ShowEnvironments = e => windows.ShowDialogAsync(new EnvironmentsDialog { DataContext = e });
            vm.ShowConflicts = c => windows.ShowDialogAsync(new ConflictsDialog { DataContext = c });
            vm.ShowWorkspaceSettings = w => windows.ShowDialogAsync(new WorkspaceSettingsDialog { DataContext = w });
            vm.ShowAdmin = a => windows.ShowDialogAsync(new AdminDialog { DataContext = a });
            vm.ShowAppAuth = app => windows.ShowDialogAsync(new AppAuthDialog { DataContext = app });
            vm.CopyToClipboard = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        };
    }

    private void OnNodeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TreeNodeViewModel node } && DataContext is MainViewModel vm) vm.Open(node);
    }

    private void OnHistoryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: HistoryRow row } && DataContext is MainViewModel vm) vm.OpenFromHistory(row);
    }
}
