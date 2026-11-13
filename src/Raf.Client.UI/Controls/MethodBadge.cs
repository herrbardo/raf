using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Raf.Client.Controls;

/// <summary>
/// Nombre de un método HTTP con su color. El color sale de los estilos (<c>:get</c>, <c>:post</c>... y
/// <c>:other</c> para métodos personalizados); el texto siempre se muestra, así el color no es la única señal.
/// </summary>
public sealed class MethodBadge : TextBlock
{
    private static readonly string[] Known = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public static readonly StyledProperty<string?> MethodProperty =
        AvaloniaProperty.Register<MethodBadge, string?>(nameof(Method));

    public MethodBadge()
    {
        TextTrimming = TextTrimming.CharacterEllipsis;
        Apply();
    }

    protected override Type StyleKeyOverride => typeof(MethodBadge);

    public string? Method
    {
        get => GetValue(MethodProperty);
        set => SetValue(MethodProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MethodProperty) Apply();
    }

    private void Apply()
    {
        var method = Method?.Trim().ToUpperInvariant() ?? "";
        Text = method;
        ToolTip.SetTip(this, method.Length > 7 ? method : null);
        var known = Array.IndexOf(Known, method) >= 0;
        foreach (var m in Known) PseudoClasses.Set(":" + m.ToLowerInvariant(), m == method);
        PseudoClasses.Set(":other", !known);
    }
}
