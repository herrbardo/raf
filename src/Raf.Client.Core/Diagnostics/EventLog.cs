using System.Text.RegularExpressions;

namespace Raf.Client.Core.Diagnostics;

public enum EventLevel { Info, Warning, Error }

/// <summary>De qué parte de la app viene el evento.</summary>
public enum EventCategory { App, Session, Connection, Sync, Updates }

/// <summary>Un evento de la consola. <see cref="Detail"/> lleva la información técnica, como el stack trace.</summary>
public sealed record AppEvent(DateTimeOffset At, EventLevel Level, EventCategory Category, string Message, string? Detail = null);

/// <summary>
/// Eventos recientes de la app, en memoria y solo de esta ejecución, para la consola. Se puede escribir desde
/// cualquier hilo. Antes de guardar se quitan tokens y contraseñas que pudieran venir en un mensaje de error.
/// </summary>
public sealed partial class EventLog(TimeProvider clock, int capacity = 1000)
{
    private readonly object _sync = new();
    private readonly LinkedList<AppEvent> _events = new();

    public EventLog() : this(TimeProvider.System)
    {
    }

    /// <summary>Se dispara en el hilo que escribió el evento.</summary>
    public event Action<AppEvent>? Added;

    public event Action? Cleared;

    public void Info(EventCategory category, string message, string? detail = null) => Add(EventLevel.Info, category, message, detail);
    public void Warning(EventCategory category, string message, string? detail = null) => Add(EventLevel.Warning, category, message, detail);
    public void Error(EventCategory category, string message, string? detail = null) => Add(EventLevel.Error, category, message, detail);

    /// <summary>Error con la excepción completa en el detalle.</summary>
    public void Error(EventCategory category, string message, Exception e) => Add(EventLevel.Error, category, message, e.ToString());

    public void Add(EventLevel level, EventCategory category, string message, string? detail = null)
    {
        var item = new AppEvent(clock.GetLocalNow(), level, category, Redact(message)!, Redact(detail));
        lock (_sync)
        {
            _events.AddLast(item);
            if (_events.Count > capacity) _events.RemoveFirst();
        }
        Added?.Invoke(item);
    }

    /// <summary>Los eventos guardados, del más viejo al más nuevo.</summary>
    public IReadOnlyList<AppEvent> Snapshot()
    {
        lock (_sync) return [.. _events];
    }

    public void Clear()
    {
        lock (_sync) _events.Clear();
        Cleared?.Invoke();
    }

    /// <summary>Tapa valores que no deben quedar a la vista: JWT, encabezados Bearer y parámetros como access_token.</summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Jwt().Replace(text, "[token oculto]");
        text = Bearer().Replace(text, "$1 [oculto]");
        return SecretParameter().Replace(text, "$1[oculto]");
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    /// <summary>Parámetros de URL (<c>access_token=...</c>) y propiedades JSON (<c>"refreshToken":"..."</c>).</summary>
    [GeneratedRegex(@"\b((?:access_token|refresh_token|accessToken|refreshToken|token|password|contraseña)""?\s*[=:]\s*""?)[^&\s"",}]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretParameter();
}
