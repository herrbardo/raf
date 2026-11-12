using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

/// <summary>Variables efectivas para el environment activo, distinguiendo las secretas.</summary>
public sealed class VariableContext
{
    public static VariableContext Empty { get; } = new([], []);

    private readonly Dictionary<string, VariableDto> _winners;
    private readonly IReadOnlyList<VariableDto> _ordered;

    private VariableContext(Dictionary<string, VariableDto> winners, IReadOnlyList<VariableDto> ordered)
    {
        _winners = winners;
        _ordered = ordered;
    }

    /// <summary>Aplica la precedencia: environment activo por encima de las globales del workspace.</summary>
    public static VariableContext From(IEnumerable<VariableDto> all, Guid? activeEnvironment)
    {
        var list = all.ToList();
        var winners = new Dictionary<string, VariableDto>(StringComparer.Ordinal);
        foreach (var v in list.Where(v => v.EnvironmentId is null)) winners[v.Key] = v;
        if (activeEnvironment is not null)
            foreach (var v in list.Where(v => v.EnvironmentId == activeEnvironment)) winners[v.Key] = v;
        return new VariableContext(winners, winners.Values.ToList());
    }

    public bool IsDefined(string key) => _winners.ContainsKey(key);

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
