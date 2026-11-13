using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Raf.Client.Core.Local;

/// <summary>
/// Variables de sesión: valores que el usuario guarda desde una respuesta (por ejemplo un token). Son de este
/// usuario en este equipo y no se sincronizan. Se separan por workspace y environment, y se guardan cifradas en el
/// almacén seguro, un registro por workspace.
/// </summary>
public sealed class SessionVariableStore(ISecureStore secure, string scope)
{
    private const string NoEnvironment = "-";

    // workspace → environment → variable → valor
    private readonly Dictionary<Guid, Dictionary<string, Dictionary<string, string>>> _loaded = [];
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
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    public IReadOnlyDictionary<string, string> Get(Guid workspace, Guid? environment)
    {
        lock (_gate)
            return Workspace(workspace).TryGetValue(Key(environment), out var values)
                ? new Dictionary<string, string>(values, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public void Set(Guid workspace, Guid? environment, string name, string value)
    {
        if (!IsValidName(name)) throw new ArgumentException(string.Format(CoreTexts.Session_InvalidName, name), nameof(name));
        Change(workspace, all =>
        {
            if (!all.TryGetValue(Key(environment), out var values))
                all[Key(environment)] = values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (values.TryGetValue(name, out var old) && old == value) return false;
            values[name] = value;
            return true;
        });
    }

    public void Remove(Guid workspace, Guid? environment, string name) =>
        Change(workspace, all => all.TryGetValue(Key(environment), out var values) && values.Remove(name));

    public void Clear(Guid workspace, Guid? environment) =>
        Change(workspace, all => all.Remove(Key(environment)));

    private void Change(Guid workspace, Func<Dictionary<string, Dictionary<string, string>>, bool> change)
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

    private Dictionary<string, Dictionary<string, string>> Workspace(Guid workspace)
    {
        if (_loaded.TryGetValue(workspace, out var all)) return all;

        all = [];
        if (secure.Read(Name(workspace)) is { } json)
        {
            try
            {
                all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? [];
            }
            catch (JsonException)
            {
                // Un registro dañado no impide trabajar: se empieza sin variables de sesión.
            }
        }
        foreach (var key in all.Keys.ToList()) all[key] = new Dictionary<string, string>(all[key], StringComparer.Ordinal);
        _loaded[workspace] = all;
        return all;
    }

    private string Name(Guid workspace) => $"session-vars-{scope}-{workspace:N}";

    private static string Key(Guid? environment) => environment?.ToString("N") ?? NoEnvironment;
}
