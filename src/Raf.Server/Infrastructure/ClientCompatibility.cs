using System.Reflection;
using Raf.Contracts;

namespace Raf.Server.Infrastructure;

/// <summary>
/// Versión mínima de cliente (<c>Clients:MinVersion</c>). Un cliente que manda una versión menor recibe 426 en
/// cualquier ruta salvo <c>/health</c>, y la app le pide actualizarse en lugar de fallar más adelante. Las
/// llamadas sin la cabecera (scripts, curl) no se controlan.
/// </summary>
public static class ClientCompatibility
{
    public const string MinVersionKey = "Clients:MinVersion";

    public static string ServerVersion { get; } = AppVersion.Display(
        typeof(ClientCompatibility).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0");

    public static IApplicationBuilder UseClientCompatibility(this WebApplication app)
    {
        var configured = app.Configuration[MinVersionKey];
        if (!AppVersion.TryParse(string.IsNullOrWhiteSpace(configured) ? "0.0.0" : configured, out var min))
            throw new InvalidOperationException($"{MinVersionKey} no es una versión válida: '{configured}'.");
        var minText = min.ToString(3);

        app.MapGet("/version", () => new ServerInfoResponse(ServerVersion, minText)).AllowAnonymous();

        return app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/health") &&
                ctx.Request.Headers[RafHeaders.ClientVersion] is [{ } sent] &&
                AppVersion.TryParse(sent, out var client) && client < min)
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status426UpgradeRequired,
                    title: AppVersion.OutdatedTitle,
                    detail: $"Esta versión de RAF ({AppVersion.Display(sent)}) es anterior a la mínima que acepta el servidor ({minText}). Actualizá la aplicación.",
                    extensions: new Dictionary<string, object?> { ["minClientVersion"] = minText }).ExecuteAsync(ctx);
                return;
            }
            await next(ctx);
        });
    }
}
