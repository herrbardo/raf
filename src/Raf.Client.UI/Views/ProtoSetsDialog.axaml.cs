using Avalonia.Interactivity;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class ProtoSetsDialog : DialogView
{
    public ProtoSetsDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    public override async Task<bool> CanCloseAsync() =>
        DataContext is not ProtoSetsViewModel { HasChanges: true } vm ||
        await vm.Dialogs.ConfirmAsync(Texts.Main_UnsavedTitle, Texts.ProtoW_CloseUnsaved, Texts.Main_Discard);
}
