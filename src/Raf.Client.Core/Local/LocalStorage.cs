using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Raf.Client.Core.Api;
using Url = Raf.Client.Core.Session.ServerUrl;

namespace Raf.Client.Core.Local;

public static class AppPaths
{
    /// <summary>
    /// Carpeta de datos locales de la app; no se sincroniza con el perfil móvil del usuario.
    /// RAF_DATA_DIR la reemplaza, para probar dos clientes con usuarios distintos en el mismo equipo.
    /// </summary>
    public static string DataDirectory { get; } =
        OperatingSystem.IsBrowser() ? "/raf" // sistema de archivos en memoria de la pestaña
        : Environment.GetEnvironmentVariable("RAF_DATA_DIR") is { Length: > 0 } custom ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAF");
}

/// <summary>Textos con nombre. En web, el almacenamiento del navegador.</summary>
public interface ITextStore
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}

/// <summary>Almacén de secretos del sistema operativo (en web, el de la pestaña).</summary>
public interface ISecureStore : ITextStore;

/// <summary>Solo memoria: la sesión no sobrevive al cierre de la app. Nunca se guardan tokens en claro.</summary>
public sealed class MemorySecureStore : ISecureStore
{
    private readonly Dictionary<string, string> _values = new();

    public string? Read(string name)
    {
        lock (_values) return _values.GetValueOrDefault(name);
    }

    public void Write(string name, string value)
    {
        lock (_values) _values[name] = value;
    }

    public void Delete(string name)
    {
        lock (_values) _values.Remove(name);
    }
}

/// <summary>Archivos cifrados con DPAPI para el usuario actual de Windows.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiSecureStore(string directory) : ISecureStore
{
    private string PathOf(string name) => Path.Combine(directory, name + ".dpapi");

    public string? Read(string name)
    {
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(PathOf(name)), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public void Write(string name, string value)
    {
        Directory.CreateDirectory(directory);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        var temp = PathOf(name) + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, PathOf(name), overwrite: true);
    }

    public void Delete(string name) => File.Delete(PathOf(name));
}

/// <summary>Elementos genéricos del Keychain de macOS.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed partial class KeychainSecureStore(string service) : ISecureStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int NotFound = -25300;

    public string? Read(string name)
    {
        var (s, a) = (Encoding.UTF8.GetBytes(service), Encoding.UTF8.GetBytes(name));
        var status = SecKeychainFindGenericPassword(
            0, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, out var item);
        if (status != 0) return null;
        try
        {
            return Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            SecKeychainItemFreeContent(0, data);
            CFRelease(item);
        }
    }

    public void Write(string name, string value)
    {
        var (s, a, v) = (Encoding.UTF8.GetBytes(service), Encoding.UTF8.GetBytes(name), Encoding.UTF8.GetBytes(value));
        var status = SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == 0)
        {
            SecKeychainItemFreeContent(0, data);
            status = SecKeychainItemModifyAttributesAndData(item, 0, (uint)v.Length, v);
            CFRelease(item);
        }
        else if (status == NotFound)
        {
            status = SecKeychainAddGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, (uint)v.Length, v, out var created);
            if (status == 0) CFRelease(created);
        }
        if (status != 0) throw new IOException(string.Format(CoreTexts.Keychain_WriteFailed, status));
    }

    public void Delete(string name)
    {
        var (s, a) = (Encoding.UTF8.GetBytes(service), Encoding.UTF8.GetBytes(name));
        if (SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item) != 0)
            return;
        SecKeychainItemFreeContent(0, data);
        SecKeychainItemDelete(item);
        CFRelease(item);
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(
        nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        out uint passwordLength, out nint password, out nint item);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(
        nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        uint passwordLength, byte[] password, out nint item);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(nint item, nint attributes, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(nint attributes, nint data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(nint item);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint handle);
}

public static class SecureStores
{
    public static ISecureStore CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new DpapiSecureStore(Path.Combine(AppPaths.DataDirectory, "secure"));
        if (OperatingSystem.IsMacOS()) return new KeychainSecureStore("RAF");
        return new MemorySecureStore();
    }
}

/// <summary>Un servidor RAF guardado en este equipo. La URL está normalizada (ver <see cref="Url"/>).</summary>
public sealed record SavedServer(string Url, string? Name = null)
{
    /// <summary>El nombre, o la URL si no tiene.</summary>
    [JsonIgnore]
    public string Title => Name ?? Url;
}

public enum ServerSaveError { None, InvalidUrl, Duplicate }

public readonly record struct ServerSaveResult(SavedServer? Server, ServerSaveError Error);

/// <summary>Opciones de ejecución de un request, propias de este equipo. No se sincronizan.</summary>
public sealed record RequestLocalOptions(int? TimeoutSeconds = null, bool AcceptInvalidCertificates = false);

public sealed class LocalSettings
{
    /// <summary>Servidor elegido la última vez: con él arranca la app. Null muestra la pantalla de servidores.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Servidores guardados en este equipo, en el orden en que se agregaron.</summary>
    public List<SavedServer> Servers { get; set; } = new();

    /// <summary>Recientes de versiones anteriores (hasta la 0.3); <see cref="MigrateServers"/> los pasa a <see cref="Servers"/>.</summary>
    public List<string>? RecentServers { get; set; }

    public string? LastEmail { get; set; }

    /// <summary>Idioma de la interfaz ("es", "en"); null sigue al sistema. Se aplica al reiniciar.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Pasa los recientes de versiones anteriores a la lista de servidores guardados y se asegura de que el
    /// servidor elegido esté en ella. Las URLs inválidas o repetidas se descartan.
    /// </summary>
    public void MigrateServers()
    {
        var urls = new List<string>(RecentServers ?? []);
        if (ServerUrl is { } current) urls.Add(current);
        RecentServers = null;

        foreach (var text in urls)
        {
            if (Url.Normalize(text) is not { } uri) continue;
            if (FindServer(uri.ToString()) is null) Servers.Add(new SavedServer(uri.ToString()));
        }

        if (ServerUrl is not null)
            ServerUrl = Url.Normalize(ServerUrl)?.ToString();
    }

    /// <summary>
    /// Agrega un servidor (<paramref name="original"/> null) o reemplaza uno guardado. Si se edita el servidor
    /// elegido, la elección sigue a la URL nueva.
    /// </summary>
    public ServerSaveResult SaveServer(SavedServer? original, string? name, string? url)
    {
        if (Url.Normalize(url) is not { } uri) return new(null, ServerSaveError.InvalidUrl);
        var text = uri.ToString();
        var index = original is null ? -1 : Servers.IndexOf(original);
        var existing = FindServer(text);
        if (existing is not null && Servers.IndexOf(existing) != index) return new(null, ServerSaveError.Duplicate);

        var saved = new SavedServer(text, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        if (index < 0)
        {
            Servers.Add(saved);
        }
        else
        {
            Servers[index] = saved;
            if (SameUrl(ServerUrl, original!.Url)) ServerUrl = text;
        }

        return new(saved, ServerSaveError.None);
    }

    /// <summary>Borra un servidor guardado; si era el elegido, la app vuelve a arrancar en la pantalla de servidores.</summary>
    public void RemoveServer(SavedServer server)
    {
        Servers.Remove(server);
        if (SameUrl(ServerUrl, server.Url)) ServerUrl = null;
    }

    private SavedServer? FindServer(string url) => Servers.FirstOrDefault(s => SameUrl(s.Url, url));

    private static bool SameUrl(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public Guid? LastWorkspaceId { get; set; }
    public int DefaultTimeoutSeconds { get; set; } = 30;

    /// <summary>Environment activo por workspace.</summary>
    public Dictionary<Guid, Guid> ActiveEnvironments { get; set; } = new();

    public Dictionary<Guid, RequestLocalOptions> RequestOptions { get; set; } = new();

    /// <summary>Qué puede hacer el asistente conectado por MCP (Plan 12). Todo apagado por defecto.</summary>
    public McpPermissions Mcp { get; set; } = new();
}

/// <summary>
/// Permisos del MCP (Plan 12, M5). Ejecutar y escribir necesitan leer; el proceso MCP los relee en cada llamada.
/// </summary>
public sealed record McpPermissions(bool Read = false, bool Run = false, bool Write = false)
{
    public bool CanRead => Read;
    public bool CanRun => Read && Run;
    public bool CanWrite => Read && Write;
}

/// <summary>Preferencias del equipo: un archivo JSON en desktop, el almacenamiento del navegador en web.</summary>
public sealed class SettingsStore
{
    private readonly object _lock = new();
    private readonly Action<string> _save;

    public SettingsStore(string path) : this(() => File.ReadAllText(path), json => WriteFile(path, json))
    {
    }

    public SettingsStore(ITextStore store, string name) : this(() => store.Read(name), json => store.Write(name, json))
    {
    }

    private SettingsStore(Func<string?> load, Action<string> save)
    {
        _save = save;
        Current = Load(load);
    }

    public LocalSettings Current { get; }

    private static LocalSettings Load(Func<string?> load)
    {
        try
        {
            var settings = load() is { } json ? JsonSerializer.Deserialize<LocalSettings>(json, ClientJson.Options) ?? new() : new();
            settings.MigrateServers();
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private static void WriteFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    public void Update(Action<LocalSettings> change)
    {
        lock (_lock)
        {
            change(Current);
            _save(JsonSerializer.Serialize(Current, ClientJson.Options));
        }
    }
}
