using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>
/// Login automático de la App (Plan 08, etapa 3): antes de un request que hereda la auth, si no hay token o
/// venció, se ejecuta el login y el token queda en una variable de sesión; ante un 401 se renueva una vez y se
/// reintenta. Todo queda en la consola del request.
/// </summary>
public sealed partial class RequestRunner
{
    /// <summary>El token se renueva un poco antes de vencer, para que no venza en pleno envío.</summary>
    public static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(30);

    /// <summary>Lo que se muestra del body de un login fallido, para entender por qué falló.</summary>
    private const int FailedBodyPreviewChars = 2048;

    private sealed record LoginOutcome(ObtainedToken? Token, RunResult? Failure);

    public async Task<RunResult> RunAsync(RunInput input, CancellationToken ct = default)
    {
        if (input.Login is not { Config.Enabled: true } login || !InheritsAuth(input.Request, input.Sample))
            return await RunOnceAsync(input, ct);

        var start = Stopwatch.GetTimestamp();
        var events = new List<RunEvent>();
        var variables = input.Variables;
        ObtainedToken? token = null;

        if (TokenNeeded(login, variables) is { } reason)
        {
            var outcome = await LoginAsync(login.Config, input, variables, reason, start, events, ct);
            if (outcome.Failure is { } failure) return failure;
            token = outcome.Token!;
            variables = variables.WithSession(token.Variable, token.Value);
        }

        var result = await RunShiftedAsync(input with { Variables = variables }, start, events, ct);
        if (token is null && result is { Outcome: RunOutcome.Completed, StatusCode: 401 })
        {
            var outcome = await LoginAsync(login.Config, input, variables, CoreTexts.Login_Renew401, start, events, ct);
            if (outcome.Failure is { } failure)
            {
                // Se muestra el 401 del request; el motivo del login fallido ya quedó en la consola.
                failure.Dispose();
                return result.With(events, null);
            }

            token = outcome.Token!;
            variables = variables.WithSession(token.Variable, token.Value);
            result.Dispose();
            result = await RunShiftedAsync(input with { Variables = variables }, start, events, ct);
        }
        return result.With(events, token);
    }

    /// <summary>Por qué hace falta el login, o null si el token guardado sirve.</summary>
    private string? TokenNeeded(AppLoginState login, VariableContext variables)
    {
        if (!variables.IsSession(login.Config.Variable)) return CoreTexts.Login_NoToken;
        return login.ExpiresAt is { } expires && _time.GetUtcNow() >= expires - RenewMargin ? CoreTexts.Login_Expired : null;
    }

    /// <summary>Ejecuta y suma sus eventos a la consola, con el momento contado desde <paramref name="start"/>.</summary>
    private async Task<RunResult> RunShiftedAsync(RunInput input, long start, List<RunEvent> events, CancellationToken ct)
    {
        var offset = Stopwatch.GetElapsedTime(start);
        var result = await RunOnceAsync(input, ct);
        events.AddRange(result.Events.Select(e => e with { At = e.At + offset }));
        return result;
    }

    private async Task<LoginOutcome> LoginAsync(
        AppLogin config, RunInput input, VariableContext variables, string reason, long start, List<RunEvent> events, CancellationToken ct)
    {
        events.Add(Event(start, RunEventLevel.Info, reason));

        var check = Check(config.Request, null, variables);
        if (check.UnreadableSecrets.Count > 0)
            return Failed(string.Format(CoreTexts.Login_UnreadableSecrets, string.Join(", ", check.UnreadableSecrets)), RunOutcome.Failed, null);

        var offset = Stopwatch.GetElapsedTime(start);
        using var login = await RunOnceAsync(
            new RunInput(config.Request with { Extractions = null }, null, variables, input.Options), ct);
        var loginEvents = login.Events
            .Select(e => e with { At = e.At + offset, Title = string.Format(CoreTexts.Login_Prefix, e.Title) })
            .ToList();

        if (login.Outcome != RunOutcome.Completed)
        {
            events.AddRange(loginEvents);
            return Failed(login.Error ?? login.Outcome.ToString(), login.Outcome, null);
        }

        var text = login.Body?.GetPreviewText();
        var status = login.StatusCode ?? 0;
        if (status is < 200 or >= 300)
        {
            events.AddRange(loginEvents);
            return Failed(string.Format(CoreTexts.Login_Status, $"{status} {login.ReasonPhrase}".TrimEnd()), RunOutcome.Failed, BodyPreview(text, variables));
        }

        var found = ResponseExtractor.Evaluate(
            new ResponseExtraction(config.Variable, config.TokenSource, config.TokenPath), text, login.Headers);
        if (found.Value is not { Length: > 0 } value)
        {
            events.AddRange(loginEvents);
            return Failed(string.Format(CoreTexts.Login_TokenNotFound, config.TokenPath.Trim()), RunOutcome.Failed,
                string.Join(Environment.NewLine, new[] { found.Problem, BodyPreview(text, variables) }.Where(s => !string.IsNullOrEmpty(s))));
        }

        // Desde acá el token es una variable de sesión: se tapa también en lo que ya mostró el login (un header, por ejemplo).
        var masked = variables.WithSession(config.Variable, value);
        events.AddRange(loginEvents.Select(e => e with { Title = masked.Scrub(e.Title), Detail = e.Detail is null ? null : masked.Scrub(e.Detail) }));

        var (expires, warning) = Expiry(config, value, text);
        events.Add(Event(start, RunEventLevel.Info, string.Format(CoreTexts.Login_TokenSaved, config.Variable),
            expires is { } at ? string.Format(CoreTexts.Login_ExpiresAt, at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            : CoreTexts.Login_NoExpiry));
        if (warning is not null) events.Add(Event(start, RunEventLevel.Warning, warning));
        return new LoginOutcome(new ObtainedToken(config.Variable, value, expires), null);

        LoginOutcome Failed(string why, RunOutcome outcome, string? detail)
        {
            var error = variables.Scrub(string.Format(CoreTexts.Login_Failed, why));
            events.Add(Event(start, RunEventLevel.Error, error, detail is null ? null : variables.Scrub(detail)));
            return new LoginOutcome(null, new RunResult
            {
                Outcome = outcome == RunOutcome.Completed ? RunOutcome.Failed : outcome,
                Elapsed = Stopwatch.GetElapsedTime(start),
                Error = error,
                Events = events.ToList(),
            });
        }
    }

    private static RunEvent Event(long start, RunEventLevel level, string title, string? detail = null) =>
        new(Stopwatch.GetElapsedTime(start), null, RunEventKind.Auth, level, title, detail);

    private static string? BodyPreview(string? text, VariableContext variables)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var preview = text.Length <= FailedBodyPreviewChars ? text : text[..FailedBodyPreviewChars] + "…";
        return variables.Scrub(string.Format(CoreTexts.Login_ResponseBody, preview));
    }

    /// <summary>Cuándo vence el token y, si no se pudo saber, un aviso: en ese caso se renueva solo ante un 401.</summary>
    private (DateTimeOffset? At, string? Warning) Expiry(AppLogin config, string token, string? body)
    {
        var now = _time.GetUtcNow();
        switch (config.Expiry)
        {
            case TokenExpiry.Fixed:
                return (now.AddMinutes(config.ExpiryMinutes ?? 0), null);
            case TokenExpiry.JwtExp:
                return JwtExpiry(token) is { } exp ? (exp, null) : (null, CoreTexts.Login_NotJwt);
            case TokenExpiry.ResponseField:
                var path = (config.ExpiryPath ?? "").Trim();
                var field = ResponseExtractor.Evaluate(new ResponseExtraction("", ExtractionSource.Body, path), body, []);
                return ParseExpiry(field.Value, now) is { } at ? (at, null) : (null, string.Format(CoreTexts.Login_ExpiryUnreadable, path));
            default:
                return (null, null);
        }
    }

    /// <summary>El <c>exp</c> de un JWT (segundos Unix), sin validar la firma: solo se usa para saber cuándo renovar.</summary>
    public static DateTimeOffset? JwtExpiry(string token)
    {
        var parts = token.Trim().Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("exp", out var exp)
                   && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Un número chico son segundos desde ahora (<c>expires_in</c>); uno grande, un instante Unix en segundos;
    /// un texto, una fecha (sin zona se toma como UTC).
    /// </summary>
    public static DateTimeOffset? ParseExpiry(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0)
        {
            try
            {
                return number >= 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds((long)number) : now.AddSeconds(number);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
}
