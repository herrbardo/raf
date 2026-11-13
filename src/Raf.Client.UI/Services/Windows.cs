using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Raf.Client.Controls;

namespace Raf.Client.Services;

/// <summary>
/// El contenido de un diálogo o de una ventana secundaria. Se muestra con <see cref="IWindowService"/>: en desktop
/// dentro de una ventana; en web, superpuesto en la página, que no tiene ventanas.
/// </summary>
public class DialogView : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogView, string?>(nameof(Title));

    private Func<object?, Task>? _close;

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Tamaño preferido. Sin alto, se ajusta al contenido.</summary>
    public double DialogWidth { get; set; } = 480;
    public double DialogHeight { get; set; } = double.NaN;
    public double DialogMinWidth { get; set; }
    public double DialogMinHeight { get; set; }
    public bool DialogCanResize { get; set; } = true;

    /// <summary>Lo conecta el servicio que lo muestra.</summary>
    internal void Attach(Func<object?, Task> close) => _close = close;

    /// <summary>Cierra el diálogo si <see cref="CanCloseAsync"/> lo permite. El resultado le llega a quien lo abrió.</summary>
    public void Close(object? result = null) => _ = RequestCloseAsync(result);

    internal async Task<bool> RequestCloseAsync(object? result)
    {
        if (!await CanCloseAsync()) return false;
        if (_close is { } close) await close(result);
        return true;
    }

    /// <summary>Por ejemplo, para confirmar si hay cambios sin guardar.</summary>
    public virtual Task<bool> CanCloseAsync() => Task.FromResult(true);

    public virtual void OnShown()
    {
    }

    public virtual void OnHidden()
    {
    }
}

/// <summary>Abre diálogos y la ventana de herramientas. Cada plataforma tiene la suya.</summary>
public interface IWindowService
{
    /// <summary>Modal: termina al cerrarse, con el resultado que pasó <see cref="DialogView.Close"/>.</summary>
    Task<object?> ShowDialogAsync(DialogView view);

    /// <summary>
    /// No modal y único por <paramref name="key"/>: si ya está abierto, se trae al frente. En web, como no hay
    /// ventanas, se muestra como diálogo.
    /// </summary>
    void ShowTool(string key, Func<DialogView> create);

    /// <summary>La ventana o página activa, para el portapapeles y los archivos.</summary>
    TopLevel? Current { get; }
}

/// <summary>Desktop: cada diálogo es una ventana del sistema.</summary>
public sealed class WindowService : IWindowService
{
    private readonly Dictionary<string, Window> _tools = [];

    private static IClassicDesktopStyleApplicationLifetime Desktop =>
        (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;

    public TopLevel? Current => Desktop.Windows.LastOrDefault(w => w.IsActive) ?? Desktop.MainWindow;

    public async Task<object?> ShowDialogAsync(DialogView view)
    {
        var owner = (Window?)Current ?? Desktop.MainWindow!;
        var window = Wrap(view);
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        return await window.ShowDialog<object?>(owner);
    }

    public void ShowTool(string key, Func<DialogView> create)
    {
        if (_tools.TryGetValue(key, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var window = Wrap(create());
        window.Icon = Desktop.MainWindow?.Icon;
        _tools[key] = window;
        window.Closed += (_, _) => _tools.Remove(key);
        if (Desktop.MainWindow is { } main)
        {
            main.Closed += (_, _) => window.Close();
            window.Show(main);
        }
        else
        {
            window.Show();
        }
    }

    private static Window Wrap(DialogView view)
    {
        var window = new Window
        {
            Content = view,
            Width = view.DialogWidth,
            MinWidth = view.DialogMinWidth,
            MinHeight = view.DialogMinHeight,
            CanResize = view.DialogCanResize,
            SizeToContent = double.IsNaN(view.DialogHeight) ? SizeToContent.Height : SizeToContent.Manual,
        };
        if (!double.IsNaN(view.DialogHeight)) window.Height = view.DialogHeight;
        window.Bind(Window.TitleProperty, view.GetObservable(DialogView.TitleProperty));

        var closing = false;
        view.Attach(result =>
        {
            closing = true;
            window.Close(result);
            return Task.CompletedTask;
        });
        // El botón de cerrar de la ventana pasa por la misma confirmación que el de la vista.
        window.Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            await view.RequestCloseAsync(null);
        };
        window.Opened += (_, _) => view.OnShown();
        window.Closed += (_, _) => view.OnHidden();
        return window;
    }
}

/// <summary>
/// Web: los diálogos se superponen a la página, con un fondo que bloquea lo de atrás. El panel donde se dibujan lo
/// registra la vista raíz (<see cref="Host"/>).
/// </summary>
public sealed class OverlayWindowService : IWindowService
{
    private const double Gutter = 24;
    private readonly Dictionary<string, DialogView> _tools = [];

    /// <summary>La capa de la vista raíz donde se dibujan los diálogos.</summary>
    public static Panel? Host { get; set; }

    public TopLevel? Current => Host is null ? null : TopLevel.GetTopLevel(Host);

    public Task<object?> ShowDialogAsync(DialogView view)
    {
        var host = Host ?? throw new InvalidOperationException("Falta la capa de diálogos de la vista raíz.");
        var done = new TaskCompletionSource<object?>();
        var previousFocus = TopLevel.GetTopLevel(host)?.FocusManager?.GetFocusedElement();
        var frame = Frame(view, host);
        var backdrop = new Border { Classes = { "overlay-backdrop" }, Child = frame, Focusable = true };
        backdrop.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            view.Close();
        };

        view.Attach(result =>
        {
            host.Children.Remove(backdrop);
            view.OnHidden();
            // El foco vuelve a donde estaba, para que sigan andando los atajos y el teclado.
            if (previousFocus is InputElement focus && focus.GetVisualRoot() is not null) focus.Focus();
            done.TrySetResult(result);
            return Task.CompletedTask;
        });
        host.Children.Add(backdrop);
        view.OnShown();
        backdrop.Focus();
        return done.Task;
    }

    public void ShowTool(string key, Func<DialogView> create)
    {
        if (_tools.ContainsKey(key)) return;
        var view = create();
        _tools[key] = view;
        _ = ShowAndForgetAsync();

        async Task ShowAndForgetAsync()
        {
            try
            {
                await ShowDialogAsync(view);
            }
            finally
            {
                _tools.Remove(key);
            }
        }
    }

    /// <summary>Título, botón de cerrar y el contenido, con el tamaño pedido sin pasarse de la página.</summary>
    private static Control Frame(DialogView view, Panel host)
    {
        var title = new TextBlock { Classes = { "heading" }, VerticalAlignment = VerticalAlignment.Center };
        title.Bind(TextBlock.TextProperty, view.GetObservable(DialogView.TitleProperty));
        var close = new Button
        {
            Classes = { "icon" },
            Content = new Icon { Data = (Geometry?)Application.Current!.FindResource("IconClose") },
        };
        ToolTip.SetTip(close, Texts.V_Close);
        close.Click += (_, _) => view.Close();

        var header = new DockPanel { Margin = new Thickness(20, 12, 12, 0) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);

        var frame = new Border
        {
            Classes = { "overlay-dialog" },
            Width = view.DialogWidth,
            MinWidth = Math.Min(view.DialogMinWidth, view.DialogWidth),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new DockPanel { Children = { header, view } },
        };
        if (!double.IsNaN(view.DialogHeight)) frame.Height = view.DialogHeight;

        void Fit()
        {
            frame.MaxWidth = Math.Max(200, host.Bounds.Width - Gutter * 2);
            frame.MaxHeight = Math.Max(160, host.Bounds.Height - Gutter * 2);
        }
        Fit();
        host.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) Fit();
        };
        return frame;
    }
}
