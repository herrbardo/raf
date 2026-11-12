using System.Text;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

public sealed record RunOptions(TimeSpan Timeout, bool AcceptInvalidCertificates = false);

/// <summary>Un request guardado y, opcionalmente, el sample que se ejecuta sobre él.</summary>
public sealed record RunInput(RequestSpec Request, SampleOverrides? Sample, VariableContext Variables, RunOptions Options);

/// <summary>Variables que el request usa y no se pueden resolver.</summary>
public sealed record RunPreflight(IReadOnlyList<string> Missing, IReadOnlyList<string> UnreadableSecrets);

public enum RunOutcome { Completed, Cancelled, TimedOut, Failed }

/// <summary>
/// Cuerpo de una respuesta. En memoria se conserva solo el inicio; si no entra, el contenido completo queda en
/// un archivo temporal que el sistema elimina al cerrarse, aun si la app termina de forma inesperada.
/// </summary>
public sealed class ResponseBody(byte[] preview, long length, FileStream? overflow, string? mediaType, string? charset) : IDisposable
{
    public byte[] Preview { get; } = preview;
    public long Length { get; } = length;
    public string? MediaType { get; } = mediaType;
    public bool IsTruncated => overflow is not null;

    public string GetPreviewText()
    {
        Encoding encoding = Encoding.UTF8;
        try
        {
            if (!string.IsNullOrWhiteSpace(charset)) encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
        }
        catch (ArgumentException)
        {
            // Charset desconocido: se muestra como UTF-8.
        }

        using var reader = new StreamReader(new MemoryStream(Preview), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public async Task SaveToAsync(Stream destination, CancellationToken ct = default)
    {
        if (overflow is null)
        {
            await destination.WriteAsync(Preview, ct);
            return;
        }
        overflow.Position = 0;
        await overflow.CopyToAsync(destination, ct);
    }

    public void Dispose() => overflow?.Dispose();
}

public sealed class RunResult : IDisposable
{
    public required RunOutcome Outcome { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
    public ResponseBody? Body { get; init; }

    /// <summary>Pasos de la ejecución para la consola de la pestaña, con los secretos enmascarados.</summary>
    public IReadOnlyList<RunEvent> Events { get; init; } = [];

    public void Dispose() => Body?.Dispose();
}
