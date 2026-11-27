using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Raf.Client.ViewModels;

namespace Raf.Client.Views;

/// <summary>
/// Copiar, cortar, pegar y arrastrar en el árbol de la colección (Ticket-041). El arrastre es propio, con eventos de
/// puntero, para que funcione igual en escritorio y en web: se marca la fila de destino (línea arriba o abajo, o el
/// fondo si va adentro) y al soltar decide el viewmodel.
/// </summary>
public partial class MainView
{
    /// <summary>Lo que hay que mover el puntero, con el botón apretado, para que un clic pase a ser un arrastre.</summary>
    private const double DragThreshold = 6;

    private TreeNodeViewModel? _dragNode;
    private Point _dragStart;
    private bool _dragging;
    private Border? _dropRow;
    private DropPosition _dropPosition;

    private void HookTreeDrag()
    {
        // Con los ya atendidos: el TreeViewItem marca como atendido el clic con el que se selecciona.
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // Solo cuando el árbol pierde la captura: al tomarla, la pierde el elemento que la tenía y eso también sube hasta acá.
        Tree.AddHandler(PointerCaptureLostEvent, (_, e) =>
        {
            if (e.Source == Tree) EndDrag();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Ctrl+C, Ctrl+X y Ctrl+V (Cmd en Mac) sobre el nodo elegido.</summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || (e.KeyModifiers & KeyModifiers.Alt) != 0) return;
        if (Tree.SelectedItem is not TreeNodeViewModel node || DataContext is not MainViewModel vm) return;

        switch (e.Key)
        {
            case Key.C:
                vm.CopyNode(node);
                break;
            case Key.X:
                vm.CutNode(node);
                break;
            case Key.V:
                _ = vm.PasteAsync(node);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        EndDrag();
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed || DataContext is not MainViewModel { CanEdit: true }) return;
        if (RowAt(e.Source as Visual) is not { DataContext: TreeNodeViewModel node }) return;
        _dragNode = node;
        _dragStart = e.GetPosition(Tree);
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode is null) return;
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }

        var position = e.GetPosition(Tree);
        if (!_dragging)
        {
            var delta = position - _dragStart;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(Tree); // con el puntero capturado, las filas se buscan por posición
        }
        ShowDrop(position);
        e.Handled = true;
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || _dragNode is not { } source)
        {
            EndDrag();
            return;
        }

        // Se vuelve a calcular con la posición final: un movimiento brusco puede no haber marcado la fila de destino.
        ShowDrop(e.GetPosition(Tree));
        var target = _dropRow?.DataContext as TreeNodeViewModel;
        var position = _dropPosition;
        e.Pointer.Capture(null);
        EndDrag();
        e.Handled = true;
        if (target is not null && DataContext is MainViewModel vm) _ = vm.DropAsync(source, target, position);
    }

    /// <summary>Marca la fila bajo el puntero si se puede soltar ahí; si no, no marca nada.</summary>
    private void ShowDrop(Point position)
    {
        ClearDropMark();
        if (DataContext is not MainViewModel vm || _dragNode is not { } source) return;
        if (RowAt(Tree.InputHitTest(position) as Visual) is not { DataContext: TreeNodeViewModel target } row) return;
        if (Tree.TranslatePoint(position, row) is not { } local || row.Bounds.Height <= 0) return;

        // En una App o carpeta, el medio es "adentro"; en el resto, arriba o abajo.
        var ratio = local.Y / row.Bounds.Height;
        var into = target.Kind is NodeKind.App or NodeKind.Folder;
        var drop = into
            ? ratio < 0.25 ? DropPosition.Before : ratio > 0.75 ? DropPosition.After : DropPosition.Into
            : ratio < 0.5 ? DropPosition.Before : DropPosition.After;
        if (!vm.CanDrop(source, target, drop)) return;

        _dropRow = row;
        _dropPosition = drop;
        row.Classes.Set("drop-before", drop == DropPosition.Before);
        row.Classes.Set("drop-after", drop == DropPosition.After);
        row.Classes.Set("drop-into", drop == DropPosition.Into);
    }

    private void ClearDropMark()
    {
        _dropRow?.Classes.RemoveAll(["drop-before", "drop-after", "drop-into"]);
        _dropRow = null;
    }

    private void EndDrag()
    {
        ClearDropMark();
        _dragNode = null;
        _dragging = false;
    }

    /// <summary>
    /// La fila del nodo que contiene a <paramref name="visual"/>. A veces el punto cae en el fondo del TreeViewItem y
    /// no en la fila: entonces se busca la fila de ese item (la primera con su DataContext, antes que las de sus hijos).
    /// </summary>
    private static Border? RowAt(Visual? visual)
    {
        if (visual is null) return null;
        var ancestors = visual.GetSelfAndVisualAncestors().ToList();
        if (ancestors.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("node-row")) is { } row) return row;
        return ancestors.OfType<TreeViewItem>().FirstOrDefault() is { } item
            ? item.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("node-row") && b.DataContext == item.DataContext)
            : null;
    }
}
