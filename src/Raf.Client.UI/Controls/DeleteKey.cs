using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Raf.Client.Controls;

/// <summary>
/// Supr (Delete) en una lista o árbol ejecuta el comando de eliminar del elemento elegido. También responde a
/// Retroceso, que es la tecla "delete" de los teclados de Mac. Si el foco está en un cuadro de texto dentro de la
/// lista, la tecla queda para el texto. El comando decide si se puede (permisos, selección) y pide confirmación.
/// </summary>
public static class DeleteKey
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(DeleteKey));

    static DeleteKey()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (e.NewValue is not null) control.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        });
    }

    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);

    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not Control control || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is not (Key.Delete or Key.Back)) return;
        if (IsInTextInput(e.Source as Visual, control)) return;
        if (GetCommand(control) is not { } command || !command.CanExecute(null)) return;

        e.Handled = true;
        command.Execute(null);
    }

    private static bool IsInTextInput(Visual? source, Control list) =>
        source is not null && source.GetSelfAndVisualAncestors().TakeWhile(v => v != list).Any(v => v is TextBox or TextField);
}
