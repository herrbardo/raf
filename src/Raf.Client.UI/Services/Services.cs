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

    /// <summary>Archivos de texto elegidos por el usuario, con su nombre como ruta. Vacío si cancela.</summary>
    Task<IReadOnlyList<PickedTextFile>> OpenTextFilesAsync(string title, string extension);

    /// <summary>
    /// Los archivos con <paramref name="extension"/> de una carpeta y sus subcarpetas, con la ruta relativa a ella
    /// (con "/"), así se conservan las rutas de los imports. Vacío si cancela.
    /// </summary>
    Task<IReadOnlyList<PickedTextFile>> OpenTextFolderAsync(string title, string extension);
}

/// <summary>Un archivo de texto elegido: su ruta (relativa, con "/") y su contenido.</summary>
public sealed record PickedTextFile(string Path, string Content);

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

    /// <summary>Tope por archivo leído: un .proto razonable no se acerca; evita cargar algo elegido por error.</summary>
    private const long MaxPickedFileSize = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<PickedTextFile>> OpenTextFilesAsync(string title, string extension)
    {
        if (windows.Current?.StorageProvider is not { CanOpen: true } storage) return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(extension) { Patterns = [$"*{extension}"] }],
        });
        var result = new List<PickedTextFile>();
        foreach (var file in files) result.Add(new PickedTextFile(file.Name, await ReadAsync(file)));
        return result;
    }

    public async Task<IReadOnlyList<PickedTextFile>> OpenTextFolderAsync(string title, string extension)
    {
        if (windows.Current?.StorageProvider is not { CanPickFolder: true } storage) return [];
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        if (folders.FirstOrDefault() is not { } root) return [];

        var result = new List<PickedTextFile>();
        async Task WalkAsync(IStorageFolder folder, string prefix)
        {
            await foreach (var item in folder.GetItemsAsync())
            {
                if (item is IStorageFolder sub) await WalkAsync(sub, $"{prefix}{sub.Name}/");
                else if (item is IStorageFile file && file.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    result.Add(new PickedTextFile(prefix + file.Name, await ReadAsync(file)));
            }
        }
        await WalkAsync(root, "");
        return result.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private static async Task<string> ReadAsync(IStorageFile file)
    {
        var size = (await file.GetBasicPropertiesAsync()).Size;
        if (size > MaxPickedFileSize) throw new ApiException(null, string.Format(Texts.Dialog_FileTooBig, file.Name));
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
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
