using Microsoft.EntityFrameworkCore;
using Raf.Domain.Entities;
using Raf.Server.Auth;
using Raf.Server.Data;

namespace Raf.Server.Workspaces;

/// <summary>
/// Filtro de permisos para rutas <c>/workspaces/{ws}</c>. Un usuario que no es miembro recibe 404
/// (no se revela que el workspace existe); un miembro sin el rol mínimo recibe 403.
/// El rol se resuelve una vez por request y queda cacheado en <see cref="HttpContext.Items"/>.
/// </summary>
public static class WorkspaceAccess
{
    private const string ItemKey = "raf.workspace-role";

    public static TBuilder RequireWorkspaceRole<TBuilder>(this TBuilder builder, WorkspaceRole minimum)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var role = await GetRoleAsync(ctx.HttpContext);
            if (role is null) return Infrastructure.Problems.NotFound("Workspace");
            if (role < minimum) return Results.Forbid();
            return await next(ctx);
        });

    public static async Task<WorkspaceRole?> GetRoleAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemKey, out var cached)) return (WorkspaceRole?)cached;

        WorkspaceRole? role = null;
        if (Guid.TryParse(http.GetRouteValue("ws") as string, out var ws))
        {
            var userId = http.User.GetUserId();
            var db = http.RequestServices.GetRequiredService<RafDbContext>();
            role = await (
                from m in db.WorkspaceMembers
                join w in db.Workspaces on m.WorkspaceId equals w.Id
                where m.WorkspaceId == ws && m.UserId == userId && !m.IsDeleted && !w.IsDeleted
                select (WorkspaceRole?)m.Role).FirstOrDefaultAsync(http.RequestAborted);
        }

        http.Items[ItemKey] = role;
        return role;
    }

    public static WorkspaceRole GetRole(this HttpContext http) =>
        http.Items[ItemKey] as WorkspaceRole? ?? throw new InvalidOperationException("Rol de workspace no resuelto.");
}
