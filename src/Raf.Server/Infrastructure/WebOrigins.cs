using Raf.Contracts;
using Raf.Server.Realtime;

namespace Raf.Server.Infrastructure;

/// <summary>
/// Sitios que pueden usar la API desde un navegador (<c>Cors:AllowedOrigins</c>), como el cliente web. Sin orígenes
/// no hay CORS y ningún sitio la puede llamar. La app de escritorio no lo necesita. Acepta una lista o un texto
/// separado por comas.
/// </summary>
public static class WebOrigins
{
    public const string Section = "Cors:AllowedOrigins";
    private const string Policy = "web";

    public static IServiceCollection AddWebOrigins(this IServiceCollection services, IConfiguration config)
    {
        var origins = Read(config);
        services.AddCors(o => o.AddPolicy(Policy, p => p
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders("Authorization", "Content-Type", RafHeaders.ClientVersion)
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));
        return services;
    }

    /// <summary>
    /// Va antes del control de versión, de la autenticación y del manejo de errores: un 426, un 401 o un error sin
    /// las cabeceras de CORS no se pueden leer desde el navegador, que los muestra como "no se puede conectar".
    /// </summary>
    public static IApplicationBuilder UseWebOrigins(this WebApplication app)
    {
        var allowed = Read(app.Configuration).ToHashSet(StringComparer.OrdinalIgnoreCase);
        app.UseCors(Policy);

        // El WebSocket del hub no pasa por CORS. Un navegador manda Origin: se acepta solo uno permitido, para que
        // otro sitio no lo abra con un token robado. La app de escritorio no manda Origin.
        return app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(ChangesHub.Path) &&
                ctx.Request.Headers.Origin is [{ } origin] && !allowed.Contains(origin))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(ctx);
        });
    }

    internal static string[] Read(IConfiguration config)
    {
        var section = config.GetSection(Section);
        var entries = section.GetChildren().Select(c => c.Value).Prepend(section.Value);
        return entries
            .SelectMany(e => (e ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Esquema, dominio y puerto, como los manda el navegador en <c>Origin</c>. Sin esquema se asume https: Railway
    /// pasa el dominio público del sitio sin él.
    /// </summary>
    private static string Normalize(string origin)
    {
        var text = origin.TrimEnd('/');
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || text.Contains('*'))
            throw new InvalidOperationException(
                $"{Section} tiene un origen inválido: '{origin}'. Va esquema y dominio (https://raf.example.com), sin comodines ni rutas.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
