using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Diagnostics;

namespace Raf.Client.ViewModels;

public sealed record EventRow(AppEvent Event)
{
    public string Time => Event.At.ToString("HH:mm:ss");
    public string Level => LevelText(Event.Level);
    public string Category => CategoryText(Event.Category);
    public string Message => Event.Message;
    public string? Detail => Event.Detail;
    public bool IsWarning => Event.Level == EventLevel.Warning;
    public bool IsError => Event.Level == EventLevel.Error;

    /// <summary>Una línea por evento, más el detalle si lo hay, para copiar y pegar en un reporte.</summary>
    public string ToText() =>
        $"{Event.At:yyyy-MM-dd HH:mm:ss} [{Level}] {Category}: {Message}"
        + (Detail is null ? "" : Environment.NewLine + Detail);

    public static string LevelText(EventLevel level) => level switch
    {
        EventLevel.Warning => "Advertencia",
        EventLevel.Error => "Error",
        _ => "Info",
    };

    public static string CategoryText(EventCategory category) => category switch
    {
        EventCategory.Session => "Sesión",
        EventCategory.Connection => "Conexión",
        EventCategory.Sync => "Sincronización",
        EventCategory.Updates => "Actualizaciones",
        _ => "App",
    };
}

public sealed record LevelFilter(string Name, EventLevel Minimum);

public sealed record CategoryFilter(string Name, EventCategory? Category);

/// <summary>
/// Consola global de eventos: qué pasó con la sesión, la conexión y la sincronización. Vive mientras la app está
/// abierta y muestra lo más nuevo arriba.
/// </summary>
public sealed partial class EventConsoleViewModel : ObservableObject
{
    private readonly EventLog _log;
    private readonly List<EventRow> _all = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private EventRow? _selected;

    [ObservableProperty] private LevelFilter _level;
    [ObservableProperty] private CategoryFilter _category;

    /// <summary>Advertencias y errores que llegaron con la consola cerrada.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnseenProblems), nameof(OpenTip))]
    private int _unseenProblems;

    [ObservableProperty] private string? _notice;

    private bool _isOpen;

    public EventConsoleViewModel(EventLog log)
    {
        _log = log;
        _level = Levels[0];
        _category = Categories[0];
        foreach (var e in log.Snapshot()) _all.Add(new EventRow(e));
        Apply();
        log.Added += e => Dispatcher.UIThread.Post(() => OnAdded(new EventRow(e)));
        log.Cleared += () => Dispatcher.UIThread.Post(OnCleared);
    }

    public IReadOnlyList<LevelFilter> Levels { get; } =
    [
        new("Todos los niveles", EventLevel.Info),
        new("Advertencias y errores", EventLevel.Warning),
        new("Solo errores", EventLevel.Error),
    ];

    public IReadOnlyList<CategoryFilter> Categories { get; } =
        [new("Todas las categorías", null), .. Enum.GetValues<EventCategory>().Select(c => new CategoryFilter(EventRow.CategoryText(c), c))];

    public ObservableCollection<EventRow> Items { get; } = [];

    public bool HasDetail => Selected?.Detail is not null;

    public bool HasUnseenProblems => UnseenProblems > 0;

    public string OpenTip => HasUnseenProblems
        ? $"Consola de eventos (Ctrl+Shift+L): {UnseenProblems} advertencias o errores nuevos"
        : "Consola de eventos (Ctrl+Shift+L)";

    /// <summary>Lo implementa la vista, que copia al portapapeles.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>La vista avisa cuando la ventana se abre o se cierra.</summary>
    public void SetOpen(bool open)
    {
        _isOpen = open;
        if (open) UnseenProblems = 0;
    }

    partial void OnLevelChanged(LevelFilter value) => Apply();
    partial void OnCategoryChanged(CategoryFilter value) => Apply();

    private bool Matches(EventRow row) =>
        row.Event.Level >= Level.Minimum && (Category.Category is null || row.Event.Category == Category.Category);

    private void Apply()
    {
        var selected = Selected;
        Items.Clear();
        for (var i = _all.Count - 1; i >= 0; i--)
            if (Matches(_all[i])) Items.Add(_all[i]);
        Selected = selected is not null && Items.Contains(selected) ? selected : null;
    }

    private void OnAdded(EventRow row)
    {
        _all.Add(row);
        if (_all.Count > 1000) _all.RemoveAt(0);
        if (Matches(row)) Items.Insert(0, row);
        if (Items.Count > 1000) Items.RemoveAt(Items.Count - 1);
        if (!_isOpen && row.Event.Level >= EventLevel.Warning) UnseenProblems++;
    }

    private void OnCleared()
    {
        _all.Clear();
        Items.Clear();
        UnseenProblems = 0;
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (CopyToClipboard is null) return;
        var text = new StringBuilder();
        foreach (var row in Items.Reverse()) text.AppendLine(row.ToText());
        await CopyToClipboard(text.ToString());
        Notice = Items.Count == 1 ? "Se copió 1 evento." : $"Se copiaron {Items.Count} eventos.";
    }

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Notice = null;
    }
}
