using Avalonia;
using Avalonia.Controls;
using Raf.Client.Services;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

/// <summary>La vista raíz: en desktop va dentro de la ventana principal; en web ocupa la página.</summary>
public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();
        OverlayWindowService.Host = DialogLayer;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ShellViewModel shell) return;
            var windows = shell.Windows;
            shell.ShowEventConsole = () => windows.ShowTool("console", () => new EventConsoleDialog { DataContext = shell.EventConsole });
            shell.ShowPreferences = vm => windows.ShowDialogAsync(new PreferencesDialog { DataContext = vm });
            shell.ShowAbout = vm => windows.ShowDialogAsync(new AboutDialog { DataContext = vm });
        };
    }

    /// <summary>
    /// Enfocable para que un clic en un área vacía deje el foco en la app: en web, sin foco el navegador no le pasa
    /// el teclado y los atajos no llegan. Por la misma razón toma el foco al cargarse.
    /// </summary>
    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Focus();
    }

    /// <summary>
    /// Los atajos pasan a la raíz (la ventana o la página): el foco puede quedar fuera de esta vista, por ejemplo
    /// después de Enviar, cuando el botón se deshabilita, o al cerrarse un diálogo.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not { } root || KeyBindings.Count == 0) return;
        var bindings = KeyBindings.ToList();
        KeyBindings.Clear();
        root.KeyBindings.AddRange(bindings);
    }
}
