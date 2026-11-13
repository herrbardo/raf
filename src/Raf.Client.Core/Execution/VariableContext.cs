using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>Variables efectivas para el environment activo, distinguiendo las secretas.</summary>
public sealed class VariableContext
{
    public static VariableContext Empty { get; } = new([], [], []);

    private readonly Dictionary<string, VariableDto> _winners;
    private readonly IReadOnlyList<VariableDto> _ordered;
    private readonly HashSet<string> _session;

    private VariableContext(Dictionary<string, VariableDto> winners, IReadOnlyList<VariableDto> ordered, HashSet<string> session)
    {
        _winners = winners;
        _ordered = ordered;
        _session = session;
    }

    /// <summary>
    /// Aplica la precedencia: variables de sesión, después el environment activo y por último las globales del
    /// workspace. Las de sesión se tratan como secretas: se enmascaran en la consola y no van al historial.
    /// </summary>
    public static VariableContext From(
        IEnumerable<VariableDto> all, Guid? activeEnvironment, IReadOnlyDictionary<string, string>? session = null)
    {
        var list = all.ToList();
        var winners = new Dictionary<string, VariableDto>(StringComparer.Ordinal);
        foreach (var v in list.Where(v => v.EnvironmentId is null)) winners[v.Key] = v;
        if (activeEnvironment is not null)
            foreach (var v in list.Where(v => v.EnvironmentId == activeEnvironment)) winners[v.Key] = v;
        var sessionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in session ?? new Dictionary<string, string>())
        {
            winners[key] = new VariableDto(Guid.Empty, activeEnvironment, key, value, IsSecret: true, 0, 0);
            sessionKeys.Add(key);
        }
        return new VariableContext(winners, winners.Values.ToList(), sessionKeys);
    }

    /// <summary>Las mismas variables con una de sesión nueva, por ejemplo el token que acaba de obtener el login.</summary>
    public VariableContext WithSession(string key, string value)
    {
        var winners = new Dictionary<string, VariableDto>(_winners, StringComparer.Ordinal)
        {
            [key] = new VariableDto(Guid.Empty, null, key, value, IsSecret: true, 0, 0),
        };
        return new VariableContext(winners, winners.Values.ToList(), new HashSet<string>(_session, StringComparer.Ordinal) { key });
    }

    public bool IsDefined(string key) => _winners.ContainsKey(key);

    /// <summary>El valor vigente sale de una variable de sesión.</summary>
    public bool IsSession(string key) => _session.Contains(key);

    /// <summary>Las variables vigentes, una por nombre, ya con la precedencia aplicada.</summary>
    public IReadOnlyList<VariableDto> Effective => _ordered;

    /// <summary>La variable existe pero el usuario no puede leer su valor (secreta, rol Viewer).</summary>
    public bool IsUnreadable(string key) => _winners.TryGetValue(key, out var v) && v.Value is null;

    /// <summary>Resolver con todos los valores legibles.</summary>
    public VariableResolver CreateResolver() => new(Readable(includeSecrets: true));

    /// <summary>Resolver que deja sin reemplazar las secretas, para lo que se guarda en disco.</summary>
    public VariableResolver CreatePublicResolver() => new(Readable(includeSecrets: false));

    /// <summary>
    /// Oculta los valores secretos que aparezcan en un texto, también escapados como en una URL o en un
    /// formulario (los espacios como +).
    /// </summary>
    public string Scrub(string text)
    {
        foreach (var v in _ordered.Where(v => v.IsSecret && !string.IsNullOrEmpty(v.Value)))
        {
            var escaped = Uri.EscapeDataString(v.Value!);
            foreach (var form in new[] { v.Value!, escaped, escaped.Replace("%20", "+", StringComparison.Ordinal) }.Distinct())
                text = text.Replace(form, "***", StringComparison.Ordinal);
        }
        return text;
    }

    private Dictionary<string, string> Readable(bool includeSecrets) =>
        _ordered.Where(v => v.Value is not null && (includeSecrets || !v.IsSecret))
            .ToDictionary(v => v.Key, v => v.Value!, StringComparer.Ordinal);
}
