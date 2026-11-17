using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Raf.Client.Core.Api;

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

/// <summary>Opciones de ejecución de un request, propias de este equipo. No se sincronizan.</summary>
public sealed record RequestLocalOptions(int? TimeoutSeconds = null, bool AcceptInvalidCertificates = false);

public sealed class LocalSettings
{
    public const int MaxRecentServers = 10;

    public string? ServerUrl { get; set; }

    /// <summary>Servidores a los que se conectó este equipo, del más reciente al más viejo. Alimentan el autocompletar.</summary>
    public List<string> RecentServers { get; set; } = new();

    public string? LastEmail { get; set; }

    /// <summary>Idioma de la interfaz ("es", "en"); null sigue al sistema. Se aplica al reiniciar.</summary>
    public string? Language { get; set; }

    /// <summary>Elige el servidor y lo pone primero en los recientes, sin repetirlo.</summary>
    public void RememberServer(string url)
    {
        ServerUrl = url;
        RecentServers.RemoveAll(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
        RecentServers.Insert(0, url);
        if (RecentServers.Count > MaxRecentServers)
            RecentServers.RemoveRange(MaxRecentServers, RecentServers.Count - MaxRecentServers);
    }
    public Guid? LastWorkspaceId { get; set; }
    public int DefaultTimeoutSeconds { get; set; } = 30;

    /// <summary>Environment activo por workspace.</summary>
    public Dictionary<Guid, Guid> ActiveEnvironments { get; set; } = new();

    public Dictionary<Guid, RequestLocalOptions> RequestOptions { get; set; } = new();
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
            return load() is { } json ? JsonSerializer.Deserialize<LocalSettings>(json, ClientJson.Options) ?? new() : new();
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
