using Avalonia.Input;

namespace Raf.Client.Controls;

/// <summary>
/// Los atajos se escriben con Ctrl; en Mac se usan con Cmd (Ticket-042). Por cada atajo con Ctrl se agrega el mismo
/// con Cmd (Meta), enlazado al comando del original para que siga sus cambios, como el Guardar de la pestaña activa.
/// Se agrega en todos los sistemas: en web no se sabe de antemano si el navegador corre en un Mac, y en Windows la
/// tecla Windows con esas letras la toma el sistema antes.
/// </summary>
public static class CommandKey
{
    public static void AddMetaAliases(IList<KeyBinding> bindings)
    {
        foreach (var binding in bindings.ToList())
        {
            if (binding.Gesture is not { } gesture || !gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) continue;
            var meta = new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
            if (bindings.Any(b => meta.Equals(b.Gesture))) continue;

            var alias = new KeyBinding { Gesture = meta };
            alias[!KeyBinding.CommandProperty] = binding[!KeyBinding.CommandProperty];
            alias[!KeyBinding.CommandParameterProperty] = binding[!KeyBinding.CommandParameterProperty];
            bindings.Add(alias);
        }
    }
}
