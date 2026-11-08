using System.Text.Json;
using Raf.Client.Core.Api;
using Raf.Client.Core.Execution;

namespace Raf.Client.Core.History;

/// <summary>Registro de una ejecución. No guarda headers, auth ni body, y la URL no lleva valores secretos.</summary>
public sealed record RunHistoryEntry(
    DateTimeOffset At,
    Guid? RequestId,
    Guid? SampleId,
    string Name,
    string Method,
    string Url,
    RunOutcome Outcome,
    int? StatusCode,
    long ElapsedMs,
    long? Size,
    string? Error);

/// <summary>Historial local por usuario, en un archivo de una entrada por línea. No se sincroniza.</summary>
public sealed class RunHistoryStore(string directory)
{
    public const int MaxEntries = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string PathOf(Guid userId) => Path.Combine(directory, $"history-{userId:N}.jsonl");

    public async Task AddAsync(Guid userId, RunHistoryEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathOf(userId);
            await File.AppendAllTextAsync(path, Serialize(entry) + Environment.NewLine, ct);

            // Se compacta cada tanto para no reescribir el archivo en cada ejecución.
            var lines = await File.ReadAllLinesAsync(path, ct);
            if (lines.Length > MaxEntries * 2)
                await File.WriteAllLinesAsync(path, lines[^MaxEntries..], ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Las ejecuciones más recientes primero.</summary>
    public async Task<IReadOnlyList<RunHistoryEntry>> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = PathOf(userId);
            if (!File.Exists(path)) return [];
            var lines = await File.ReadAllLinesAsync(path, ct);
            return lines.Reverse().Take(MaxEntries).Select(Parse).OfType<RunHistoryEntry>().ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Serialize(RunHistoryEntry entry) => JsonSerializer.Serialize(entry, ClientJson.Options);

    private static RunHistoryEntry? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<RunHistoryEntry>(line, ClientJson.Options);
        }
        catch (JsonException)
        {
            return null; // una línea dañada no invalida el resto
        }
    }
}
