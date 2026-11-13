using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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
            vm.ShowEnvironments = ShowEnvironmentsAsync;
            vm.ShowConflicts = ShowConflictsAsync;
            vm.ShowWorkspaceSettings = ShowWorkspaceSettingsAsync;
            vm.ShowAdmin = ShowAdminAsync;
            vm.ShowAppAuth = app => new AppAuthWindow { DataContext = app }.ShowDialog((Window)this.GetVisualRoot()!);
            vm.CopyToClipboard = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        };
    }

    private Task ShowEnvironmentsAsync(EnvironmentsViewModel vm) =>
        new EnvironmentsWindow { DataContext = vm }.ShowDialog((Window)this.GetVisualRoot()!);

    private Task ShowConflictsAsync(ConflictsViewModel vm) =>
        new ConflictsWindow { DataContext = vm }.ShowDialog((Window)this.GetVisualRoot()!);

    private Task ShowWorkspaceSettingsAsync(WorkspaceSettingsViewModel vm) =>
        new WorkspaceSettingsWindow { DataContext = vm }.ShowDialog((Window)this.GetVisualRoot()!);

    private Task ShowAdminAsync(AdminViewModel vm) =>
        new AdminWindow { DataContext = vm }.ShowDialog((Window)this.GetVisualRoot()!);

    private void OnNodeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TreeNodeViewModel node } && DataContext is MainViewModel vm) vm.Open(node);
    }

    private void OnHistoryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: HistoryRow row } && DataContext is MainViewModel vm) vm.OpenFromHistory(row);
    }
}
