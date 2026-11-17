using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Raf.Contracts;
using Raf.Server.Auth;

namespace Raf.Server.Relay;

/// <summary>
/// <c>POST /relay</c>: el cliente web le pasa al servidor cada salto HTTP ya armado (variables resueltas, auth
/// aplicada) y recibe la respuesta. No se guardan ni se registran headers ni bodies: pueden traer secretos.
/// </summary>
public static class RelayEndpoints
{
    private const string RateLimitPolicy = "relay";

    public static IServiceCollection AddRelay(this IServiceCollection services)
    {
        services.AddOptions<RelayOptions>().BindConfiguration(RelayOptions.Section);
        services.AddSingleton<RelaySender>();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.FindFirst(RafClaims.Subject)?.Value ?? "",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, ctx.RequestServices.GetRequiredService<IOptions<RelayOptions>>().Value.PerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
        return services;
    }

    public static void MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/relay", async (
                RelayRequest request, RelaySender sender, IOptions<RelayOptions> options, ClaimsPrincipal user,
                ILoggerFactory logs, CancellationToken ct) =>
            {
                var log = logs.CreateLogger("Raf.Server.Relay");
                if (!options.Value.Enabled)
                    return Failure(RelayErrors.Disabled, StatusCodes.Status503ServiceUnavailable,
                        "Este servidor no envía requests por el cliente web.");
                try
                {
                    var response = await sender.SendAsync(request, ct);
                    log.LogInformation("Relay de {User}: {Method} {Host} → {Status}",
                        user.GetUserId(), request.Method, HostOf(request.Url), response.StatusCode);
                    return Results.Ok(response);
                }
                catch (RelayException e)
                {
                    log.LogInformation("Relay de {User}: {Method} {Host} → {Error}",
                        user.GetUserId(), request.Method, HostOf(request.Url), e.Kind);
                    return Failure(e.Kind, e.Status, e.Message);
                }
            })
            .WithTags("Relay")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy);
    }

    private static IResult Failure(string kind, int status, string detail) =>
        Results.Problem(statusCode: status, title: "No se pudo enviar el request", detail: detail,
            extensions: new Dictionary<string, object?> { [RelayErrors.Extension] = kind });

    /// <summary>Solo el host: la URL puede llevar secretos en el path o la query.</summary>
    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "?";
}
