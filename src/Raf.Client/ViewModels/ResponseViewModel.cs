using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Execution;
using Raf.Client.Services;

namespace Raf.Client.ViewModels;

public sealed record ResponseHeaderRow(string Name, string Value);

public sealed partial class ResponseViewModel(IDialogs dialogs) : ObservableObject, IDisposable
{
    private RunResult? _result;
    private FormattedText? _formatted;
    private string _raw = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder), nameof(HasBody))]
    private bool _hasResult;

    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _isSuccess;
    [ObservableProperty] private string? _timeText;
    [ObservableProperty] private string? _sizeText;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _truncatedText;
    [ObservableProperty] private string? _bodyText;
    [ObservableProperty] private string? _syntax;
    [ObservableProperty] private bool _showRaw;
    [ObservableProperty] private IReadOnlyList<ResponseHeaderRow> _headers = [];

    public bool ShowPlaceholder => !IsRunning && !HasResult;

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
        SizeText = result.Body is { } b ? FormatSize(b.Length) : null;
        Headers = result.Headers.Select(h => new ResponseHeaderRow(h.Key, h.Value)).ToList();

        _raw = result.Body?.GetPreviewText() ?? "";
        _formatted = ResponseFormatter.Format(_raw, result.Body?.MediaType);
        TruncatedText = result.Body is { IsTruncated: true } body
            ? $"Se muestran los primeros {FormatSize(body.Preview.Length)} de {FormatSize(body.Length)}. Guardá el archivo para ver el contenido completo."
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
        await using var stream = await dialogs.SaveFileAsync("respuesta" + Extension(body.MediaType));
        if (stream is null) return;
        try
        {
            await body.SaveToAsync(stream);
        }
        catch (IOException e)
        {
            await dialogs.InfoAsync("No se pudo guardar", e.Message);
        }
    }

    private static string Extension(string? mediaType) =>
        mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? ".json"
        : mediaType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true ? ".xml"
        : mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true ? ".html"
        : mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ? ".txt"
        : ".bin";

    public static string OutcomeText(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Cancelled => "Cancelado",
        RunOutcome.TimedOut => "Timeout",
        RunOutcome.Failed => "Error",
        _ => "Completado",
    };

    public static string FormatTime(TimeSpan t) =>
        t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.00} s" : $"{t.TotalMilliseconds:0} ms";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B",
    };

    public void Dispose() => _result?.Dispose();
}
