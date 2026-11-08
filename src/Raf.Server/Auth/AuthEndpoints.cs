using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Raf.Contracts;
using Raf.Server.Data;
using Raf.Server.Infrastructure;

namespace Raf.Server.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/login", async (
            LoginRequest req, UserManager<AppUser> users, RafDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            var invalid = Results.Problem(statusCode: 401, title: "Email o contraseña incorrectos.");
            var user = string.IsNullOrWhiteSpace(req.Email) ? null : await users.FindByEmailAsync(req.Email.Trim());
            if (user is null || user.IsDisabled) return invalid;

            if (await users.IsLockedOutAsync(user))
                return Results.Problem(statusCode: 423, title: "Cuenta bloqueada temporalmente por intentos fallidos.");

            if (!await users.CheckPasswordAsync(user, req.Password))
            {
                await users.AccessFailedAsync(user);
                return invalid;
            }

            // Con logins simultáneos del mismo usuario el reset puede chocar por concurrencia: Identity no
            // guarda pero deja el usuario como modificado. Se descarta ese cambio para que no se reintente
            // al guardar el refresh token; el contador se resetea en el próximo login.
            if (!(await users.ResetAccessFailedCountAsync(user)).Succeeded)
                db.Entry(user).State = EntityState.Detached;

            return Results.Ok(await tokens.IssueAsync(user, ct));
        }).AllowAnonymous();

        auth.MapPost("/refresh", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
            await tokens.RefreshAsync(req.RefreshToken, ct) is { } result
                ? Results.Ok(result)
                : Results.Problem(statusCode: 401, title: "Sesión inválida o expirada."))
            .AllowAnonymous();

        auth.MapPost("/logout", async (LogoutRequest req, TokenService tokens, CancellationToken ct) =>
        {
            await tokens.RevokeAsync(req.RefreshToken, ct);
            return Results.NoContent();
        }).AllowAnonymous();

        auth.MapPost("/change-password", async (
            ChangePasswordRequest req, ClaimsPrincipal principal, UserManager<AppUser> users,
            TokenService tokens, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(principal.GetUserId().ToString());
            if (user is null || user.IsDisabled) return Results.Unauthorized();

            var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
            if (!result.Succeeded) return result.ToProblem();

            user.MustChangePassword = false;
            await users.UpdateAsync(user);
            await tokens.RevokeAllAsync(user.Id, ct);
            return Results.Ok(await tokens.IssueAsync(user, ct));
        }).RequireAuthorization(Policies.AllowPasswordChange);

        app.MapGet("/me", async (ClaimsPrincipal principal, RafDbContext db, CancellationToken ct) =>
            await db.Users.FindAsync([principal.GetUserId()], ct) is { } user
                ? Results.Ok(user.ToDto())
                : Results.Unauthorized())
            .WithTags("Auth")
            .RequireAuthorization(Policies.AllowPasswordChange);

        app.MapGet("/users", async (string? search, RafDbContext db, CancellationToken ct) =>
        {
            var q = db.Users.Where(u => !u.IsDisabled);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim()}%";
                q = q.Where(u => EF.Functions.ILike(u.Email!, pattern) || EF.Functions.ILike(u.DisplayName, pattern));
            }
            var list = await q.OrderBy(u => u.DisplayName).Take(20).ToListAsync(ct);
            return list.Select(u => u.ToSummary());
        })
            .WithTags("Users")
            .WithSummary("Busca usuarios habilitados por email o nombre (para agregar miembros).")
            .RequireAuthorization();
    }

    public static IResult ToProblem(this IdentityResult result) =>
        Results.ValidationProblem(
            result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()),
            title: "No se pudo completar la operación.");
}
