using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Raf.Server.Data;

namespace Raf.Server.Auth;

/// <summary>
/// Corte inmediato (D3): en cada request con JWT se consulta el usuario por clave primaria, sin caché. Un usuario
/// deshabilitado o borrado queda rechazado con 401 aunque su access token siga vigente, y quien perdió el rol Admin
/// deja de usarlo en el acto.
/// </summary>
public static class UserStateCheck
{
    public static async Task ValidateAsync(TokenValidatedContext ctx)
    {
        if (!Guid.TryParse(ctx.Principal?.FindFirstValue(RafClaims.Subject), out var userId))
        {
            ctx.Fail("Token sin usuario.");
            return;
        }

        var db = ctx.HttpContext.RequestServices.GetRequiredService<RafDbContext>();
        var state = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsDisabled, u.IsAdmin })
            .FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);

        if (state is null || state.IsDisabled)
        {
            ctx.Fail("Usuario deshabilitado.");
            return;
        }

        if (!state.IsAdmin && ctx.Principal!.Identity is ClaimsIdentity identity)
            foreach (var claim in identity.FindAll(RafClaims.Admin).ToList()) identity.RemoveClaim(claim);
    }
}
