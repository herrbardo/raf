using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Raf.Client.Core.Api;
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
    IDialogs Dialogs);

public interface IDialogs
{
    Task<string?> PromptAsync(string title, string label, string initial = "");
    Task<bool> ConfirmAsync(string title, string message, string accept = "Aceptar");
    Task InfoAsync(string title, string message);

    /// <summary>Muestra un valor que no se vuelve a ver (como una contraseña temporal), con botón para copiarlo.</summary>
    Task ShowSecretAsync(string title, string message, string secret);
    Task<Stream?> SaveFileAsync(string suggestedName);
}

public sealed class WindowDialogs : IDialogs
{
    private static Window Owner =>
        ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows
            .LastOrDefault(w => w.IsActive) is { } active ? active
            : ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!;

    public async Task<string?> PromptAsync(string title, string label, string initial = "")
    {
        var input = new TextBox { Text = initial };
        var dialog = Build<string?>(title, label, "Aceptar", true, input, () => input.Text, null, out var window);
        window.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) window.Close(input.Text);
        };
        var result = await dialog();
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept = "Aceptar") =>
        Build(title, message, accept, true, null, () => true, false, out _)();

    public Task InfoAsync(string title, string message) =>
        Build(title, message, "Aceptar", false, null, () => true, false, out _)();

    public async Task ShowSecretAsync(string title, string message, string secret)
    {
        var value = new TextBox { Text = secret, IsReadOnly = true, Classes = { "mono" } };
        var copy = new Button { Content = "Copiar" };
        var row = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        copy.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(copy);
        row.Children.Add(value);

        var dialog = Build(title, message, "Listo", false, row, () => true, false, out var window);
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(window)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(secret);
                copy.Content = "Copiada ✓";
            }
        };
        window.Opened += (_, _) => { value.Focus(); value.SelectAll(); };
        await dialog();
    }

    public async Task<Stream?> SaveFileAsync(string suggestedName)
    {
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            Title = "Guardar respuesta",
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    private static Func<Task<T>> Build<T>(
        string title, string message, string accept, bool cancellable, Control? body,
        Func<T> onAccept, T onCancel, out Window window)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

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
            var cancel = new Button { Content = "Cancelar", IsCancel = true };
            cancel.Click += (_, _) => dialog.Close(onCancel);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        // Misma estructura que las ventanas en AXAML: contenido con margen y botones en el pie.
        var content = new StackPanel { Margin = new Thickness(20, 18), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (body is not null) content.Children.Add(body);
        var footer = new Border { Classes = { "dialog-footer" }, Child = buttons };
        DockPanel.SetDock(footer, Dock.Bottom);
        dialog.Content = new DockPanel { Children = { footer, content } };
        window = dialog;

        return async () =>
        {
            var result = await dialog.ShowDialog<object?>(Owner);
            return result is T value ? value : onCancel;
        };
    }
}
