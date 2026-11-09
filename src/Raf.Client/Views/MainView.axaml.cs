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
        };
    }

    private Task ShowEnvironmentsAsync(EnvironmentsViewModel vm) =>
        new EnvironmentsWindow { DataContext = vm }.ShowDialog((Window)this.GetVisualRoot()!);

    private Task ShowConflictsAsync(ConflictsViewModel vm) =>
        new ConflictsWindow { DataContext = vm }.ShowDialog((Window)this.GetVisualRoot()!);

    private void OnNodeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TreeNodeViewModel node } && DataContext is MainViewModel vm) vm.Open(node);
    }

    private void OnHistoryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: HistoryRow row } && DataContext is MainViewModel vm) vm.OpenFromHistory(row);
    }
}
