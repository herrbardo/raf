using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Execution;
using Raf.Client.Services;

namespace Raf.Client.ViewModels;

/// <summary>Un header de la respuesta, con lo que ofrece su menú contextual.</summary>
public sealed partial class ResponseHeaderRow(string name, string value, ResponseViewModel owner)
{
    public string Name { get; } = name;
    public string Value { get; } = value;

    public string ToText() => $"{Name}: {Value}";

    [RelayCommand] private Task CopyName() => owner.CopyAsync(Name);
    [RelayCommand] private Task CopyValue() => owner.CopyAsync(Value);
    [RelayCommand] private Task CopyLine() => owner.CopyAsync(ToText());
    [RelayCommand] private Task CopyAll() => owner.CopyAsync(string.Join(Environment.NewLine, owner.Headers.Select(h => h.ToText())));
    [RelayCommand] private Task SaveToVariable() => owner.SaveToVariableAsync(Name, Value);
}

/// <summary>Una línea de la consola del request.</summary>
public sealed record RunEventRow(RunEvent Event)
{
    public string At => "+" + RunFormat.Time(Event.At);
    public string? Duration => Event.Duration is { } d ? RunFormat.Time(d) : null;
    public string Stage => StageText(Event.Kind);
    public string Title => Event.Title;
    public string? Detail => Event.Detail;
    public bool HasDetail => Detail is not null;
    public bool IsWarning => Event.Level == RunEventLevel.Warning;
    public bool IsError => Event.Level == RunEventLevel.Error;

    /// <summary>El evento y su detalle, para copiar y pegar en un reporte.</summary>
    public string ToText() =>
        $"{At} [{Stage}] {Title}" + (Duration is null ? "" : $" ({Duration})")
        + (Detail is null ? "" : Environment.NewLine + Detail);

    public static string StageText(RunEventKind kind) => kind switch
    {
        RunEventKind.Variables => Texts.Stage_Variables,
        RunEventKind.Request => Texts.Stage_Request,
        RunEventKind.Dns => Texts.Stage_Dns,
        RunEventKind.Connect => Texts.Stage_Connect,
        RunEventKind.Tls => Texts.Stage_Tls,
        RunEventKind.Reused => Texts.Stage_Connect,
        RunEventKind.Response => Texts.Stage_Response,
        RunEventKind.Redirect => Texts.Stage_Redirect,
        RunEventKind.Body => Texts.Stage_Body,
        RunEventKind.Error => Texts.Stage_Error,
        RunEventKind.Auth => Texts.Stage_Auth,
        RunEventKind.Extraction => Texts.Stage_Extraction,
        _ => Texts.Stage_Total,
    };
}

public sealed partial class ResponseViewModel(IDialogs dialogs) : ObservableObject, IDisposable
{
    private RunResult? _result;
    private FormattedText? _formatted;
    private string _raw = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder), nameof(ShowSending))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder), nameof(ShowSending), nameof(HasBody))]
    private bool _hasResult;

    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isSuccess;

    /// <summary>El servidor respondió con 4xx o 5xx.</summary>
    [ObservableProperty] private bool _isHttpError;

    /// <summary>No hubo respuesta (timeout, cancelado o error de red): ni éxito ni código de error.</summary>
    [ObservableProperty] private bool _isNoResponse;
    [ObservableProperty] private string? _timeText;
    [ObservableProperty] private string? _sizeText;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _truncatedText;
    [ObservableProperty] private string? _bodyText;
    [ObservableProperty] private string? _syntax;
    [ObservableProperty] private bool _showRaw;
    [ObservableProperty] private IReadOnlyList<ResponseHeaderRow> _headers = [];

    /// <summary>Consola de la última ejecución. No se guarda: se pierde al cerrar la pestaña.</summary>
    [ObservableProperty] private IReadOnlyList<RunEventRow> _events = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDetail))]
    private RunEventRow? _selectedEvent;

    [ObservableProperty] private string? _consoleNotice;

    public bool HasSelectedDetail => SelectedEvent?.Detail is not null;

    /// <summary>Lo implementa la vista, que copia al portapapeles.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Guarda una variable de sesión. Lanza <see cref="ArgumentException"/> si el nombre no sirve.</summary>
    public Action<string, string>? SaveVariable { get; set; }

    public bool ShowPlaceholder => !IsRunning && !HasResult;

    /// <summary>Primer envío, sin una respuesta anterior para mostrar mientras tanto.</summary>
    public bool ShowSending => IsRunning && !HasResult;

    public bool HasBody => HasResult && _result?.Body is not null;

    public void Show(RunResult result)
    {
        _result?.Dispose();
        _result = result;

        HasResult = true;
        ErrorText = result.Error;
        TimeText = FormatTime(result.Elapsed);
        StatusText = result.StatusCode is { } code ? $"{code} {result.ReasonPhrase}".TrimEnd() : OutcomeText(result.Outcome);
        IsSuccess = result.StatusCode is >= 200 and < 400;
        IsHttpError = result.StatusCode is { } status && status is < 200 or >= 400;
        IsNoResponse = result.StatusCode is null;
        SizeText = result.Body is { } b ? FormatSize(b.Length) : null;
        Headers = result.Headers.Select(h => new ResponseHeaderRow(h.Key, h.Value, this)).ToList();
        Events = result.Events.Select(e => new RunEventRow(e)).ToList();
        // Se abre con el request enviado elegido: es lo primero que se quiere ver.
        SelectedEvent = Events.FirstOrDefault(e => e.Event.Kind == RunEventKind.Request) ?? Events.FirstOrDefault();
        ConsoleNotice = null;

        _raw = result.Body?.GetPreviewText() ?? "";
        _formatted = ResponseFormatter.Format(_raw, result.Body?.MediaType);
        IsStructured = _formatted.Format != ResponseFormat.Plain;
        PathMenuText = _formatted.Format == ResponseFormat.Xml ? Texts.Response_CopyPathXml : Texts.Response_CopyPathJson;
        MenuNode = null;
        TruncatedText = result.Body is { IsTruncated: true } body
            ? string.Format(Texts.Response_Truncated, FormatSize(body.Preview.Length), FormatSize(body.Length))
            : null;
        OnPropertyChanged(nameof(HasBody));
        Render();
    }

    partial void OnShowRawChanged(bool value) => Render();

    private void Render()
    {
        BodyText = ShowRaw || _formatted is null ? _raw : _formatted.Text;
        Syntax = ShowRaw ? null : _formatted?.Format switch
        {
            ResponseFormat.Json => "Json",
            ResponseFormat.Xml => "XML",
            _ => null,
        };
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_result?.Body is not { } body) return;
        await using var stream = await dialogs.SaveFileAsync(Texts.Response_FileName + Extension(body.MediaType));
        if (stream is null) return;
        try
        {
            await body.SaveToAsync(stream);
        }
        catch (IOException e)
        {
            await dialogs.InfoAsync(Texts.Response_SaveFailed, e.Message);
        }
    }

    // ---- Menú contextual del body ----

    /// <summary>El body es JSON o XML válido: el menú ofrece valor, clave y ruta.</summary>
    [ObservableProperty] private bool _isStructured;
    [ObservableProperty] private string _pathMenuText = Texts.Response_CopyPath;

    /// <summary>El nodo de la línea donde se abrió el menú; null si no hay ninguno.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMenuNode), nameof(HasMenuKey))]
    [NotifyCanExecuteChangedFor(nameof(CopyNodeValueCommand), nameof(CopyNodeKeyCommand), nameof(CopyNodePathCommand), nameof(SaveNodeToVariableCommand))]
    private ResponseNode? _menuNode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectionCommand))]
    private string? _menuSelection;

    private string _menuLine = "";

    public bool HasMenuNode => MenuNode is not null;
    public bool HasMenuKey => MenuNode?.Key is not null;
    private bool HasMenuSelection => !string.IsNullOrEmpty(MenuSelection);

    /// <summary>Lo llama la vista antes de abrir el menú, con la posición del clic (o del cursor) y la selección.</summary>
    public void PrepareBodyMenu(int offset, string? selection)
    {
        var text = BodyText ?? "";
        offset = Math.Clamp(offset, 0, text.Length);
        MenuNode = _formatted is { } f ? ResponseLocator.Find(text, f.Format, offset) : null;
        MenuSelection = selection;

        var start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var end = text.IndexOf('\n', offset);
        _menuLine = text[start..(end < 0 ? text.Length : end)].TrimEnd('\r');
    }

    [RelayCommand(CanExecute = nameof(HasMenuNode))]
    private Task CopyNodeValue() => CopyAsync(MenuNode?.Value);

    [RelayCommand(CanExecute = nameof(HasMenuKey))]
    private Task CopyNodeKey() => CopyAsync(MenuNode?.Key);

    [RelayCommand(CanExecute = nameof(HasMenuNode))]
    private Task CopyNodePath() => CopyAsync(MenuNode?.Path);

    [RelayCommand(CanExecute = nameof(HasMenuNode))]
    private Task SaveNodeToVariable() => MenuNode is { } node ? SaveToVariableAsync(node.Key, node.Value) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasMenuSelection))]
    private Task CopySelection() => CopyAsync(MenuSelection);

    /// <summary>Pide el nombre (sugiere la clave) y guarda el valor en una variable de sesión.</summary>
    public async Task SaveToVariableAsync(string? suggestedName, string value)
    {
        if (SaveVariable is null) return;
        var suggestion = new string((suggestedName ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-').ToArray());
        var name = await dialogs.PromptAsync(Texts.Response_SaveVariableTitle, Texts.Response_SaveVariableLabel, suggestion);
        if (name is null) return;
        try
        {
            SaveVariable(name, value);
        }
        catch (ArgumentException)
        {
            await dialogs.InfoAsync(Texts.Response_SaveVariableTitle, string.Format(CoreTexts.Session_InvalidName, name));
        }
    }

    [RelayCommand]
    private Task CopyBodyLine() => CopyAsync(_menuLine);

    [RelayCommand]
    private Task CopyBody() => CopyAsync(BodyText);

    public Task CopyAsync(string? text) =>
        CopyToClipboard is { } copy && text is not null ? copy(text) : Task.CompletedTask;

    [RelayCommand]
    private async Task CopyConsoleAsync()
    {
        if (CopyToClipboard is null || Events.Count == 0) return;
        var text = string.Join(Environment.NewLine, Events.Select(e => e.ToText()));
        await CopyToClipboard(text + Environment.NewLine);
        ConsoleNotice = Events.Count == 1 ? Texts.Events_CopiedOne : string.Format(Texts.Events_CopiedMany, Events.Count);
    }

    private static string Extension(string? mediaType) =>
        mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? ".json"
        : mediaType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true ? ".xml"
        : mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true ? ".html"
        : mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ? ".txt"
        : ".bin";

    public static string OutcomeText(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Cancelled => Texts.Outcome_Cancelled,
        RunOutcome.TimedOut => Texts.Outcome_TimedOut,
        RunOutcome.Failed => Texts.Outcome_Failed,
        _ => Texts.Outcome_Completed,
    };

    public static string FormatTime(TimeSpan t) => RunFormat.Time(t);

    public static string FormatSize(long bytes) => RunFormat.Size(bytes);

    public void Dispose() => _result?.Dispose();
}
