using System.Text.RegularExpressions;

namespace Raf.Domain.Requests;

/// <summary>
/// Reemplaza <c>{{nombre}}</c> por su valor. Precedencia: environment activo &gt; globales del workspace.
/// Los valores pueden referenciar otras variables (hasta <see cref="MaxDepth"/> niveles).
/// Las variables desconocidas quedan sin reemplazar y se informan en <see cref="Missing"/>.
/// </summary>
public sealed partial class VariableResolver
{
    public const int MaxDepth = 10;

    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

    public VariableResolver(IReadOnlyDictionary<string, string> values) => _values = values;

    public static VariableResolver FromLayers(
        IEnumerable<KeyValuePair<string, string>> workspaceGlobals,
        IEnumerable<KeyValuePair<string, string>>? environment)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in workspaceGlobals) dict[k] = v;
        if (environment is not null)
            foreach (var (k, v) in environment) dict[k] = v;
        return new VariableResolver(dict);
    }

    /// <summary>Variables referenciadas que no tienen valor.</summary>
    public IReadOnlyCollection<string> Missing => _missing;

    public string Resolve(string? text) => text is null ? "" : Resolve(text, 0);

    private string Resolve(string text, int depth) =>
        Placeholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (!_values.TryGetValue(name, out var value))
            {
                _missing.Add(name);
                return m.Value;
            }
            return depth >= MaxDepth ? value : Resolve(value, depth + 1);
        });

    public RequestSpec Resolve(RequestSpec spec) => spec with
    {
        Url = Resolve(spec.Url),
        Headers = ResolveItems(spec.Headers),
        QueryParams = ResolveItems(spec.QueryParams),
        Body = spec.Body with
        {
            Content = ResolveNullable(spec.Body.Content),
            Form = spec.Body.Form is null ? null : ResolveItems(spec.Body.Form),
        },
        Auth = spec.Auth with
        {
            Token = ResolveNullable(spec.Auth.Token),
            Username = ResolveNullable(spec.Auth.Username),
            Password = ResolveNullable(spec.Auth.Password),
            ApiKeyName = ResolveNullable(spec.Auth.ApiKeyName),
            ApiKeyValue = ResolveNullable(spec.Auth.ApiKeyValue),
        },
    };

    private string? ResolveNullable(string? s) => s is null ? null : Resolve(s);

    private IReadOnlyList<KeyValueItem> ResolveItems(IReadOnlyList<KeyValueItem> items) =>
        items.Select(i => i with { Key = Resolve(i.Key), Value = Resolve(i.Value) }).ToList();

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}")]
    private static partial Regex Placeholder();
}
