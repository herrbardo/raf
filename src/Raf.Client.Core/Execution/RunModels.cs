using System.Text;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

public sealed record RunOptions(TimeSpan Timeout, bool AcceptInvalidCertificates = false);

/// <summary>
/// Un request guardado y, opcionalmente, el sample que se ejecuta sobre él. <see cref="AppAuth"/> es la
/// autenticación de su App, que se usa si el request la hereda; <see cref="Login"/>, su login automático.
/// </summary>
public sealed record RunInput(
    RequestSpec Request, SampleOverrides? Sample, VariableContext Variables, RunOptions Options, AuthSpec? AppAuth = null,
    AppLoginState? Login = null);

/// <summary>El login automático de la App y cuándo vence el token guardado (null si no se sabe).</summary>
public sealed record AppLoginState(AppLogin Config, DateTimeOffset? ExpiresAt);

/// <summary>Token que obtuvo el login de la App durante una ejecución, para guardarlo en la variable de sesión.</summary>
public sealed record ObtainedToken(string Variable, string Value, DateTimeOffset? ExpiresAt);

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

    /// <summary>Valores que las extracciones del request sacaron de la respuesta, por nombre de variable de sesión.</summary>
    public IReadOnlyDictionary<string, string> Extracted { get; init; } = new Dictionary<string, string>();

    /// <summary>El token que obtuvo el login de la App, si se ejecutó y funcionó.</summary>
    public ObtainedToken? Token { get; init; }

    /// <summary>El mismo resultado con otros eventos y token; el body pasa al nuevo.</summary>
    internal RunResult With(IReadOnlyList<RunEvent> events, ObtainedToken? token) => new()
    {
        Outcome = Outcome, Elapsed = Elapsed, Error = Error, StatusCode = StatusCode, ReasonPhrase = ReasonPhrase,
        Headers = Headers, Body = Body, Events = events, Extracted = Extracted, Token = token,
    };

    public void Dispose() => Body?.Dispose();
}
