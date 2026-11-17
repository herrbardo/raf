using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class EnvironmentsDialog : DialogView
{
    public EnvironmentsDialog() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // La variable recién agregada recibe el foco en la clave, para escribirla directamente.
    private void OnKeyLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: VariableRowViewModel { FocusKeyOnShow: true } row } key) return;
        row.FocusKeyOnShow = false;
        key.BringIntoView();
        key.Focus(NavigationMethod.Tab);
    }

    public override async Task<bool> CanCloseAsync() =>
        DataContext is not EnvironmentsViewModel { HasChanges: true } vm ||
        await vm.Dialogs.ConfirmAsync(Texts.Main_UnsavedTitle, Texts.Env_CloseUnsaved, Texts.Main_Discard);
}
