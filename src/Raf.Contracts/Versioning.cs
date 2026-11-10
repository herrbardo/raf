namespace Raf.Contracts;

/// <summary>Versión del servidor y versión mínima de cliente que acepta. <c>GET /version</c>, sin autenticación.</summary>
public sealed record ServerInfoResponse(string Version, string MinClientVersion);

public static class RafHeaders
{
    /// <summary>Versión del cliente que hace la llamada. Si es menor a la mínima, el servidor responde 426.</summary>
    public const string ClientVersion = "X-Raf-Client-Version";
}

public static class AppVersion
{
    /// <summary>Título del 426 que devuelve el servidor a un cliente demasiado viejo.</summary>
    public const string OutdatedTitle = "Cliente desactualizado";

    /// <summary>
    /// Lee la parte numérica de una versión semántica ("1.4.0", "1.4.0-beta.2", "1.4.0+abc123"). Se ignoran la
    /// etiqueta de prerelease y los metadatos: alcanza para comparar contra la versión mínima.
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>Versión semántica sin metadatos de build, para mostrar.</summary>
    public static string Display(string? text) => (text ?? "").Split('+')[0];
}
