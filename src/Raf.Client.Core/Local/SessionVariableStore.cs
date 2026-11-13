using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raf.Client.Core.Local;

/// <summary>Una variable de sesión. <see cref="ExpiresAt"/> lo tienen los tokens que obtiene el login de una App.</summary>
public sealed record SessionVariable(string Name, string Value, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Variables de sesión: valores que el usuario guarda desde una respuesta (por ejemplo un token). Son de este
/// usuario en este equipo y no se sincronizan. Se separan por workspace y environment, y se guardan cifradas en el
/// almacén seguro, un registro por workspace.
/// </summary>
public sealed class SessionVariableStore(ISecureStore secure, string scope)
{
    private const string NoEnvironment = "-";

    /// <summary>Cómo se guarda cada variable. Los registros anteriores al vencimiento tienen solo el valor, como texto.</summary>
    private sealed record Entry(
        [property: JsonPropertyName("v")] string Value,
        [property: JsonPropertyName("e")] DateTimeOffset? ExpiresAt = null);

    // workspace → environment → variable → valor
    private readonly Dictionary<Guid, Dictionary<string, Dictionary<string, Entry>>> _loaded = [];
    private readonly Lock _gate = new();

    /// <summary>Cambió alguna variable de sesión.</summary>
    public event Action? Changed;

    /// <summary>Las de un usuario en un servidor, separadas de las de cualquier otra combinación.</summary>
    public static SessionVariableStore For(ISecureStore secure, Uri server, Guid userId)
    {
        var serverHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.ToString())))[..16];
        return new SessionVariableStore(secure, $"{serverHash}-{userId:N}");
    }

    /// <summary>Un nombre que se puede usar como <c>{{nombre}}</c>.</summary>
    public static bool IsValidName(string? name) => Raf.Domain.Requests.VariableNames.IsValid(name);

    public IReadOnlyDictionary<string, string> Get(Guid workspace, Guid? environment)
    {
        lock (_gate)
            return ValuesOf(workspace, environment) is { } values
                ? values.ToDictionary(e => e.Key, e => e.Value.Value, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Las variables con su vencimiento, para mostrarlas.</summary>
    public IReadOnlyList<SessionVariable> List(Guid workspace, Guid? environment)
    {
        lock (_gate)
            return ValuesOf(workspace, environment) is { } values
                ? values.Select(e => new SessionVariable(e.Key, e.Value.Value, e.Value.ExpiresAt)).ToList()
                : [];
    }

    /// <summary>Cuándo vence; null si no existe o no tiene vencimiento.</summary>
    public DateTimeOffset? ExpiresAt(Guid workspace, Guid? environment, string name)
    {
        lock (_gate)
            return ValuesOf(workspace, environment) is { } values && values.TryGetValue(name, out var entry) ? entry.ExpiresAt : null;
    }

    /// <summary>Guarda el valor. Sin <paramref name="expiresAt"/>, la variable queda sin vencimiento.</summary>
    public void Set(Guid workspace, Guid? environment, string name, string value, DateTimeOffset? expiresAt = null)
    {
        if (!IsValidName(name)) throw new ArgumentException(string.Format(CoreTexts.Session_InvalidName, name), nameof(name));
        var entry = new Entry(value, expiresAt);
        Change(workspace, all =>
        {
            if (!all.TryGetValue(Key(environment), out var values))
                all[Key(environment)] = values = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (values.TryGetValue(name, out var old) && old == entry) return false;
            values[name] = entry;
            return true;
        });
    }

    public void Remove(Guid workspace, Guid? environment, string name) =>
        Change(workspace, all => all.TryGetValue(Key(environment), out var values) && values.Remove(name));

    public void Clear(Guid workspace, Guid? environment) =>
        Change(workspace, all => all.Remove(Key(environment)));

    private Dictionary<string, Entry>? ValuesOf(Guid workspace, Guid? environment) =>
        Workspace(workspace).GetValueOrDefault(Key(environment));

    private void Change(Guid workspace, Func<Dictionary<string, Dictionary<string, Entry>>, bool> change)
    {
        lock (_gate)
        {
            var all = Workspace(workspace);
            if (!change(all)) return;
            foreach (var empty in all.Where(e => e.Value.Count == 0).Select(e => e.Key).ToList()) all.Remove(empty);
            if (all.Count == 0) secure.Delete(Name(workspace));
            else secure.Write(Name(workspace), JsonSerializer.Serialize(all));
        }
        Changed?.Invoke();
    }

    private Dictionary<string, Dictionary<string, Entry>> Workspace(Guid workspace)
    {
        if (_loaded.TryGetValue(workspace, out var all)) return all;

        all = [];
        if (secure.Read(Name(workspace)) is { } json)
        {
            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(json) ?? [];
                foreach (var (environment, values) in raw)
                {
                    var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                    foreach (var (name, value) in values)
                    {
                        if (value.ValueKind == JsonValueKind.String) entries[name] = new Entry(value.GetString()!);
                        else if (value.Deserialize<Entry>() is { Value: not null } entry) entries[name] = entry;
                    }
                    all[environment] = entries;
                }
            }
            catch (JsonException)
            {
                // Un registro dañado no impide trabajar: se empieza sin variables de sesión.
                all = [];
            }
        }
        _loaded[workspace] = all;
        return all;
    }

    private string Name(Guid workspace) => $"session-vars-{scope}-{workspace:N}";

    private static string Key(Guid? environment) => environment?.ToString("N") ?? NoEnvironment;
}
