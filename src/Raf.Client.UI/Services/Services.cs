using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Raf.Client.Core.Api;
using Raf.Client.Core.Diagnostics;
using Raf.Client.Core.Execution;
using Raf.Client.Core.History;
using Raf.Client.Core.Local;
using Raf.Client.Core.Session;
using Raf.Client.Core.Workspaces;

namespace Raf.Client.Services;

public sealed record ClientServices(
    SessionManager Session,
    RafApiClient Api,
    IWorkspaceStore Store, // al entrar a la app se reemplaza por la caché local del usuario
    ISecureStore Secure,
    SettingsStore Settings,
    RequestRunner Runner,
    RunHistoryStore History,
    IDialogs Dialogs,
    IWindowService Windows,
    EventLog Events);

public interface IDialogs
{
    Task<string?> PromptAsync(string title, string label, string initial = "");
    Task<bool> ConfirmAsync(string title, string message, string? accept = null);
    Task InfoAsync(string title, string message);

    /// <summary>Muestra un valor que no se vuelve a ver (como una contraseña temporal), con botón para copiarlo.</summary>
    Task ShowSecretAsync(string title, string message, string secret);
    Task<Stream?> SaveFileAsync(string suggestedName);
}

/// <summary>Diálogos simples armados en código. Se muestran con <see cref="IWindowService"/>, así sirven en desktop y en web.</summary>
public sealed class Dialogs(IWindowService windows) : IDialogs
{
    public async Task<string?> PromptAsync(string title, string label, string initial = "")
    {
        var input = new TextBox { Text = initial };
        var dialog = Build(title, label, Texts.Common_Accept, true, input, () => input.Text);
        dialog.AttachedToVisualTree += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) dialog.Close(input.Text);
        };
        var result = await windows.ShowDialogAsync(dialog) as string;
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string? accept = null) =>
        await windows.ShowDialogAsync(Build(title, message, accept ?? Texts.Common_Accept, true, null, () => true)) is true;

    public Task InfoAsync(string title, string message) =>
        windows.ShowDialogAsync(Build(title, message, Texts.Common_Accept, false, null, () => true));

    public async Task ShowSecretAsync(string title, string message, string secret)
    {
        var value = new TextBox { Text = secret, IsReadOnly = true, Classes = { "mono" } };
        var copy = new Button { Content = Texts.Common_Copy };
        var row = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        copy.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(copy);
        row.Children.Add(value);

        var dialog = Build(title, message, Texts.Common_Done, false, row, () => true);
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(dialog)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(secret);
                copy.Content = Texts.Common_Copied;
            }
        };
        dialog.AttachedToVisualTree += (_, _) =>
        {
            value.Focus();
            value.SelectAll();
        };
        await windows.ShowDialogAsync(dialog);
    }

    public async Task<Stream?> SaveFileAsync(string suggestedName)
    {
        if (windows.Current?.StorageProvider is not { CanSave: true } storage) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            Title = Texts.Dialog_SaveResponse,
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    private static DialogView Build(
        string title, string message, string accept, bool cancellable, Control? body, Func<object?> onAccept)
    {
        var dialog = new DialogView { Title = title, DialogWidth = 420, DialogCanResize = false };

        var ok = new Button { Content = accept, IsDefault = body is null, Classes = { "accent" } };
        ok.Click += (_, _) => dialog.Close(onAccept());
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        if (cancellable)
        {
            var cancel = new Button { Content = Texts.Common_Cancel, IsCancel = true };
            cancel.Click += (_, _) => dialog.Close();
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        // Misma estructura que los diálogos en AXAML: contenido con margen y botones en el pie.
        var content = new StackPanel { Margin = new Thickness(20, 18), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (body is not null) content.Children.Add(body);
        var footer = new Border { Classes = { "dialog-footer" }, Child = buttons };
        DockPanel.SetDock(footer, Dock.Bottom);
        dialog.Content = new DockPanel { Children = { footer, content } };
        return dialog;
    }
}
