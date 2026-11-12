using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
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
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(8, 6);
        FontSize = 13;
        MinHeight = 32;
        Bind(FontFamilyProperty, this.GetResourceObservable("RafMonoFont"));
        Bind(BorderBrushProperty, this.GetResourceObservable("RafBorderBrush"));
        Bind(ForegroundProperty, this.GetResourceObservable("RafTextBrush"));
        TextArea.TextView[!AvaloniaEdit.Rendering.TextView.LinkTextForegroundBrushProperty] = this.GetResourceObservable("RafAccentTextBrush").ToBinding();
        TextArea.Bind(AvaloniaEdit.Editing.TextArea.SelectionBrushProperty, this.GetResourceObservable("RafAccentSoftBrush"));
        ApplyBackground();
        ActualThemeVariantChanged += (_, _) => OnThemeChanged();

        _colorizer = new Colorizer(this);
        TextArea.TextView.LineTransformers.Add(_colorizer);
        TextChanged += OnEditorTextChanged;
        TextArea.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        TextArea.TextEntered += OnTextEntered;
        // Un carácter que no puede ir en un nombre (como } o un espacio) termina la variable: se cierra la lista.
        TextArea.TextEntering += (_, e) =>
        {
            if (_completion is not null && e.Text is { Length: > 0 } t && !t.All(VariableCompletion.IsNameChar)) _completion.Close();
        };
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
        else if (change.Property == SyntaxProperty) ApplySyntax();
        else if (change.Property == IsReadOnlyProperty) ApplyBackground();
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

    /// <summary>Solo lectura (Viewer, respuesta) con otro fondo, para distinguirlo de un campo editable.</summary>
    private void ApplyBackground() =>
        Bind(BackgroundProperty, this.GetResourceObservable(IsReadOnly ? "RafInputReadOnlyBrush" : "RafInputBrush"));

    private void ApplySyntax()
    {
        if (Syntax is not { Length: > 0 } name)
        {
            SyntaxHighlighting = null;
            return;
        }
        SyntaxPalette.Apply(this);
        SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(name);
    }

    /// <summary>Los colores de variables y de sintaxis salen del tema: al cambiarlo se vuelven a pintar.</summary>
    private void OnThemeChanged()
    {
        _colorizer.Refresh();
        if (SyntaxHighlighting is not null)
        {
            SyntaxPalette.Apply(this);
            var current = SyntaxHighlighting;
            SyntaxHighlighting = null; // fuerza a volver a colorear con la paleta nueva
            SyntaxHighlighting = current;
        }
        Redraw();
    }

    internal IBrush? ThemeBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private void ApplyMode()
    {
        HorizontalScrollBarVisibility = Multiline ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = Multiline ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        ShowLineNumbers = false;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = OpenCompletion(onlyRightAfterBraces: false);
            return;
        }
        // Con la lista abierta, Enter elige la variable: lo maneja la lista.
        if (!Multiline && e.Key == Key.Enter && _completion is null) e.Handled = true;
    }

    private CompletionWindow? _completion;

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == "{") OpenCompletion(onlyRightAfterBraces: true);
    }

    /// <summary>
    /// Abre la lista de variables si el cursor está dentro de un <c>{{</c> sin cerrar. Al escribir, solo justo
    /// después de <c>{{</c>; con Ctrl+Espacio, también en medio de un nombre, que se filtra y se reemplaza entero.
    /// </summary>
    private bool OpenCompletion(bool onlyRightAfterBraces)
    {
        if (_completion is not null || IsReadOnly || Variables is not { } variables) return false;

        var caret = CaretOffset;
        var line = Document.GetLineByOffset(caret);
        if (VariableCompletion.FindQuery(Document.GetText(line), caret - line.Offset) is not var (start, end)) return false;
        if (onlyRightAfterBraces && (start != caret - line.Offset || end != start)) return false;

        var suggestions = VariableCompletion.Suggestions(variables);
        if (suggestions.Count == 0) return false;

        var window = new CompletionWindow(TextArea)
        {
            StartOffset = line.Offset + start,
            EndOffset = line.Offset + end,
            MinWidth = 280,
        };
        foreach (var suggestion in suggestions) window.CompletionList.CompletionData.Add(new VariableCompletionData(suggestion));
        window.Closed += (_, _) => _completion = null;
        _completion = window;
        window.Show();

        var query = Document.GetText(window.StartOffset, caret - window.StartOffset);
        if (query.Length > 0) window.CompletionList.SelectItem(query);
        return true;
    }

    private sealed class VariableCompletionData(VariableSuggestion suggestion) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => suggestion.Key;
        public object Content => suggestion.Key;
        public object Description => suggestion.Description;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var document = textArea.Document;
            var line = document.GetLineByOffset(completionSegment.EndOffset);
            var after = document.GetText(completionSegment.EndOffset, line.EndOffset - completionSegment.EndOffset);
            var insertion = VariableCompletion.Insertion(suggestion.Key, after);
            var offset = completionSegment.Offset;
            document.Replace(completionSegment, insertion);

            // Si el cierre ya estaba, el cursor queda después de él, como si se lo hubiera escrito.
            var close = insertion.EndsWith("}}", StringComparison.Ordinal) ? 0 : after.IndexOf("}}", StringComparison.Ordinal) + 2;
            textArea.Caret.Offset = offset + insertion.Length + close;
        }
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
        private IBrush? _known, _missing, _unreadable;

        public void Refresh() => _known = _missing = _unreadable = null;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (owner.Variables is not { } variables) return;
            var known = _known ??= owner.ThemeBrush("RafSuccessBrush");
            var missing = _missing ??= owner.ThemeBrush("RafDangerBrush");
            var unreadable = _unreadable ??= owner.ThemeBrush("RafWarningBrush");

            var text = CurrentContext.Document.GetText(line);
            foreach (Match m in Placeholder().Matches(text))
            {
                var key = m.Groups[1].Value;
                var brush = !variables.IsDefined(key) ? missing : variables.IsUnreadable(key) ? unreadable : known;
                ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, e =>
                {
                    if (brush is not null) e.TextRunProperties.SetForegroundBrush(brush);
                    e.TextRunProperties.SetTypeface(new Typeface(
                        e.TextRunProperties.Typeface.FontFamily, weight: FontWeight.SemiBold));
                });
            }
        }

        [GeneratedRegex(VariableResolver.PlaceholderPattern)]
        private static partial Regex Placeholder();
    }
}

/// <summary>
/// Pinta las definiciones de sintaxis de AvaloniaEdit (JSON y XML) con los colores del tema. Las definiciones son
/// globales y hay un solo tema activo por vez, así que alcanza con reescribirlas al cambiar de tema.
/// </summary>
internal static class SyntaxPalette
{
    private static ThemeVariant? _applied;

    public static void Apply(TextField field)
    {
        var theme = field.ActualThemeVariant;
        if (theme == _applied) return;
        foreach (var name in new[] { "Json", "XML" })
        {
            if (HighlightingManager.Instance.GetDefinition(name) is not { } definition) continue;
            foreach (var color in definition.NamedHighlightingColors)
                if (field.ThemeBrush(KeyFor(color.Name)) is ISolidColorBrush brush)
                    color.Foreground = new SimpleHighlightingBrush(brush.Color);
        }
        _applied = theme;
    }

    private static string KeyFor(string name)
    {
        bool Has(string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has("Comment")) return "RafSyntaxCommentBrush";
        if (Has("FieldName") || Has("AttributeName") || Has("Property")) return "RafSyntaxPropertyBrush";
        if (Has("String") || Has("AttributeValue") || Has("CData")) return "RafSyntaxStringBrush";
        if (Has("Number") || Has("Digit")) return "RafSyntaxNumberBrush";
        if (Has("Bool") || Has("Null") || Has("Keyword") || Has("Entity")) return "RafSyntaxKeywordBrush";
        if (Has("Tag") || Has("DocType") || Has("Declaration")) return "RafSyntaxTagBrush";
        return "RafSyntaxPunctuationBrush";
    }
}
