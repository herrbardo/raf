using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

public partial class ResponseView : UserControl
{
    /// <summary>Offset del último clic derecho en el body; null si el menú se abre con el teclado.</summary>
    private int? _rightClickOffset;

    public ResponseView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ResponseViewModel vm)
                vm.CopyToClipboard = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        };
        BodyField.TextArea.AddHandler(PointerPressedEvent, OnBodyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Recuerda dónde se hizo clic derecho, para que el menú hable de esa línea y no de la del cursor.</summary>
    private void OnBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var view = BodyField.TextArea.TextView;
        if (!e.GetCurrentPoint(view).Properties.IsRightButtonPressed)
        {
            _rightClickOffset = null;
            return;
        }
        var position = view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset);
        _rightClickOffset = position is { } p ? BodyField.Document.GetOffset(p.Location) : null;
    }

    private void OnBodyMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not ResponseViewModel vm) return;
        var offset = _rightClickOffset ?? BodyField.CaretOffset;
        _rightClickOffset = null;
        vm.PrepareBodyMenu(offset, BodyField.SelectedText);
    }
}
