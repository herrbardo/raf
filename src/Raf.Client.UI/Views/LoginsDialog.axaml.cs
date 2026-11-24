using System.ComponentModel;
using Avalonia.Interactivity;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class LoginsDialog : DialogView
{
    public LoginsDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Sin permiso de edición no hay nada que ofrecer, ni con una lista vacía.
    private void OnListMenuOpening(object? sender, CancelEventArgs e) =>
        e.Cancel = DataContext is not LoginsViewModel { CanEdit: true, HasSelection: true };

    public override async Task<bool> CanCloseAsync() =>
        DataContext is not LoginsViewModel { HasChanges: true } vm ||
        await vm.Dialogs.ConfirmAsync(Texts.Main_UnsavedTitle, Texts.LoginsW_CloseUnsaved, Texts.Main_Discard);
}
