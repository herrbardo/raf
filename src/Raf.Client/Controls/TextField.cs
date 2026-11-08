using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using Raf.Client.Core.Execution;
using Raf.Domain.Requests;

namespace Raf.Client.Controls;

/// <summary>Variables vigentes para resaltar los campos. Avisa cuando cambian.</summary>
public sealed class VariableLookup
{
    public VariableContext Context { get; private set; } = VariableContext.Empty;

    public event Action? Changed;

    public void Update(VariableContext context)
    {
        Context = context;
        Changed?.Invoke();
    }
}

public static class VariableHighlight
{
    /// <summary>Se define en la raíz de la pantalla y lo heredan todos los campos.</summary>
    public static readonly AttachedProperty<VariableLookup?> LookupProperty =
        AvaloniaProperty.RegisterAttached<Control, VariableLookup?>("Lookup", typeof(VariableHighlight), inherits: true);

    public static VariableLookup? GetLookup(Control c) => c.GetValue(LookupProperty);

    public static void SetLookup(Control c, VariableLookup? value) => c.SetValue(LookupProperty, value);
}

/// <summary>Editor de texto con <c>{{variables}}</c> resaltadas; en una línea, o multilínea con sintaxis.</summary>
public sealed partial class TextField : TextEditor
{
    public static readonly StyledProperty<string?> BoundTextProperty =
        AvaloniaProperty.Register<TextField, string?>(nameof(BoundText), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> MultilineProperty =
        AvaloniaProperty.Register<TextField, bool>(nameof(Multiline));

    public static readonly StyledProperty<string?> SyntaxProperty =
        AvaloniaProperty.Register<TextField, string?>(nameof(Syntax));

    public static readonly StyledProperty<bool> HighlightVariablesProperty =
        AvaloniaProperty.Register<TextField, bool>(nameof(HighlightVariables), defaultValue: true);

    private readonly Colorizer _colorizer;
    private VariableLookup? _lookup;
    private bool _syncing;

    public TextField()
    {
        // Valores locales: el control usa la clave de estilo de TextEditor y sus temas tienen prioridad.
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Padding = new Thickness(6, 5);
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        FontSize = 13;
        MinHeight = 32;
        Bind(BorderBrushProperty, this.GetResourceObservable("TextControlBorderBrush"));

        _colorizer = new Colorizer(this);
        TextArea.TextView.LineTransformers.Add(_colorizer);
        TextChanged += OnEditorTextChanged;
        TextArea.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ApplyMode();
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public string? BoundText
    {
        get => GetValue(BoundTextProperty);
        set => SetValue(BoundTextProperty, value);
    }

    public bool Multiline
    {
        get => GetValue(MultilineProperty);
        set => SetValue(MultilineProperty, value);
    }

    public string? Syntax
    {
        get => GetValue(SyntaxProperty);
        set => SetValue(SyntaxProperty, value);
    }

    public bool HighlightVariables
    {
        get => GetValue(HighlightVariablesProperty);
        set => SetValue(HighlightVariablesProperty, value);
    }

    internal VariableContext? Variables => HighlightVariables ? _lookup?.Context : null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundTextProperty) PushToEditor();
        else if (change.Property == MultilineProperty) ApplyMode();
        else if (change.Property == SyntaxProperty)
            SyntaxHighlighting = Syntax is { Length: > 0 } name ? HighlightingManager.Instance.GetDefinition(name) : null;
        else if (change.Property == VariableHighlight.LookupProperty) AttachLookup(change.GetNewValue<VariableLookup?>());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachLookup(VariableHighlight.GetLookup(this));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        AttachLookup(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachLookup(VariableLookup? lookup)
    {
        if (_lookup is not null) _lookup.Changed -= Redraw;
        _lookup = lookup;
        if (_lookup is not null) _lookup.Changed += Redraw;
        Redraw();
    }

    private void Redraw() => TextArea.TextView.Redraw();

    private void ApplyMode()
    {
        HorizontalScrollBarVisibility = Multiline ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = Multiline ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        ShowLineNumbers = false;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Multiline && e.Key == Key.Enter) e.Handled = true;
    }

    private void PushToEditor()
    {
        if (_syncing) return;
        var value = BoundText ?? "";
        if (Document.TextLength == value.Length && Document.Text == value) return;
        _syncing = true;
        Document = new TextDocument(value);
        _syncing = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing) return;
        var text = Document.Text;
        if (!Multiline && text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            // Texto pegado con saltos de línea en un campo de una línea.
            Dispatcher.UIThread.Post(() => Document.Text = Document.Text.ReplaceLineEndings(" "));
            text = text.ReplaceLineEndings(" ");
        }

        _syncing = true;
        SetCurrentValue(BoundTextProperty, text);
        _syncing = false;
    }

    private sealed partial class Colorizer(TextField owner) : DocumentColorizingTransformer
    {
        private static readonly IBrush Known = new SolidColorBrush(Color.Parse("#2E9E6B"));
        private static readonly IBrush Missing = new SolidColorBrush(Color.Parse("#E5484D"));
        private static readonly IBrush Unreadable = new SolidColorBrush(Color.Parse("#D9822B"));

        protected override void ColorizeLine(DocumentLine line)
        {
            if (owner.Variables is not { } variables) return;

            var text = CurrentContext.Document.GetText(line);
            foreach (Match m in Placeholder().Matches(text))
            {
                var key = m.Groups[1].Value;
                var brush = !variables.IsDefined(key) ? Missing : variables.IsUnreadable(key) ? Unreadable : Known;
                ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, e =>
                {
                    e.TextRunProperties.SetForegroundBrush(brush);
                    e.TextRunProperties.SetTypeface(new Typeface(
                        e.TextRunProperties.Typeface.FontFamily, weight: FontWeight.SemiBold));
                });
            }
        }

        [GeneratedRegex(VariableResolver.PlaceholderPattern)]
        private static partial Regex Placeholder();
    }
}
