using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Raf.Contracts;
using Raf.Domain.Entities;
using Raf.Server.Admin;
using Raf.Server.Auth;
using Raf.Server.Data;
using Raf.Server.Infrastructure;

namespace Raf.Server.Workspaces;

public static class WorkspaceEndpoints
{
    /// <summary>Mapea <c>/workspaces</c> y devuelve el grupo <c>/workspaces/{ws}</c> (rol mínimo Viewer).</summary>
    public static RouteGroupBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/workspaces").WithTags("Workspaces").RequireAuthorization();

        root.MapGet("/", async (ClaimsPrincipal me, RafDbContext db, CancellationToken ct) =>
        {
            var userId = me.GetUserId();
            return await (
                from m in db.WorkspaceMembers
                join w in db.Workspaces on m.WorkspaceId equals w.Id
                where m.UserId == userId && !m.IsDeleted && !w.IsDeleted
                orderby w.Name
                select new WorkspaceDto(w.Id, w.Name, m.Role, w.Version, w.ChangeSeq)).ToListAsync(ct);
        });

        root.MapPost("/", async (CreateWorkspaceRequest req, ClaimsPrincipal me, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;

            var workspace = new Workspace { Id = req.Id ?? Guid.CreateVersion7(), Name = req.Name.Trim() };
            workspace.WorkspaceId = workspace.Id;
            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.Id,
                UserId = me.GetUserId(),
                Role = WorkspaceRole.Owner,
            });
            await db.SaveChangesAsync(ct);

            return Results.Created($"/workspaces/{workspace.Id}",
                new WorkspaceDto(workspace.Id, workspace.Name, WorkspaceRole.Owner, workspace.Version, workspace.ChangeSeq));
        }).WithSummary("Crea un workspace; quien lo crea queda como Owner.");

        var ws = root.MapGroup("/{ws:guid}").RequireWorkspaceRole(WorkspaceRole.Viewer);

        ws.MapGet("/", async (Guid ws, HttpContext http, RafDbContext db, CancellationToken ct) =>
        {
            var w = await db.Workspaces.FirstAsync(x => x.Id == ws, ct);
            return new WorkspaceDto(w.Id, w.Name, http.GetRole(), w.Version, w.ChangeSeq);
        });

        ws.MapPut("/", async (Guid ws, UpdateWorkspaceRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            var w = await db.Workspaces.FirstAsync(x => x.Id == ws, ct);
            if (w.Version != req.Version) return Problems.VersionConflict(w);

            w.Name = req.Name.Trim();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new WorkspaceDto(w.Id, w.Name, WorkspaceRole.Owner, w.Version, w.ChangeSeq));
        }).RequireWorkspaceRole(WorkspaceRole.Owner);

        ws.MapDelete("/", async (Guid ws, RafDbContext db, CancellationToken ct) =>
        {
            var w = await db.Workspaces.FirstAsync(x => x.Id == ws, ct);
            w.IsDeleted = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireWorkspaceRole(WorkspaceRole.Owner);

        // ---------- Miembros ----------

        ws.MapGet("/members", async (Guid ws, RafDbContext db, CancellationToken ct) =>
            await (
                from m in db.WorkspaceMembers
                join u in db.Users on m.UserId equals u.Id
                where m.WorkspaceId == ws && !m.IsDeleted
                orderby m.Role descending, u.DisplayName
                select new MemberDto(u.Id, u.Email!, u.DisplayName, m.Role)).ToListAsync(ct))
            .WithTags("Members");

        ws.MapPost("/members", async (Guid ws, AddMemberRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (req.Role == WorkspaceRole.Owner)
                return Problems.BadRequest("Para hacer Owner a alguien usá la transferencia de propiedad.");
            var user = await db.Users.FindAsync([req.UserId], ct);
            if (user is null || user.IsDisabled) return Problems.BadRequest("El usuario no existe o está deshabilitado.");

            var existing = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == ws && m.UserId == req.UserId, ct);
            if (existing is { IsDeleted: false }) return Problems.Conflict("El usuario ya es miembro del workspace.");

            if (existing is null)
            {
                db.WorkspaceMembers.Add(new WorkspaceMember
                {
                    Id = Guid.CreateVersion7(), WorkspaceId = ws, UserId = req.UserId, Role = req.Role,
                });
            }
            else
            {
                existing.IsDeleted = false;
                existing.Role = req.Role;
            }
            await db.SaveChangesAsync(ct);
            return Results.Created($"/workspaces/{ws}/members/{user.Id}",
                new MemberDto(user.Id, user.Email!, user.DisplayName, req.Role));
        }).WithTags("Members").RequireWorkspaceRole(WorkspaceRole.Owner);

        ws.MapPut("/members/{userId:guid}", async (Guid ws, Guid userId, UpdateMemberRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (req.Role == WorkspaceRole.Owner)
                return Problems.BadRequest("Para hacer Owner a alguien usá la transferencia de propiedad.");
            var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == ws && m.UserId == userId && !m.IsDeleted, ct);
            if (member is null) return Problems.NotFound("Miembro");
            if (member.Role == WorkspaceRole.Owner)
                return Problems.BadRequest("El rol del Owner cambia solo al transferir la propiedad.");

            member.Role = req.Role;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Members").RequireWorkspaceRole(WorkspaceRole.Owner);

        ws.MapDelete("/members/{userId:guid}", async (
            Guid ws, Guid userId, ClaimsPrincipal me, HttpContext http, RafDbContext db, CancellationToken ct) =>
        {
            var leaving = userId == me.GetUserId();
            if (!leaving && http.GetRole() != WorkspaceRole.Owner) return Results.Forbid();

            var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == ws && m.UserId == userId && !m.IsDeleted, ct);
            if (member is null) return Problems.NotFound("Miembro");
            if (member.Role == WorkspaceRole.Owner)
                return Problems.BadRequest("El Owner no puede salir del workspace: primero transferí la propiedad.");

            member.IsDeleted = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Members").WithSummary("El Owner quita a un miembro, o un miembro sale del workspace.");

        ws.MapPost("/transfer", async (Guid ws, TransferOwnershipRequest req, RafDbContext db, CancellationToken ct) =>
        {
            var isMember = await (
                from m in db.WorkspaceMembers
                join u in db.Users on m.UserId equals u.Id
                where m.WorkspaceId == ws && m.UserId == req.UserId && !m.IsDeleted && !u.IsDisabled
                select m).AnyAsync(ct);
            if (!isMember) return Problems.BadRequest("El nuevo Owner debe ser un miembro habilitado del workspace.");

            await WorkspaceOwnership.SetOwnerAsync(db, ws, req.UserId, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Members").WithSummary("Transfiere la propiedad. El Owner actual queda como Editor.")
          .RequireWorkspaceRole(WorkspaceRole.Owner);

        return ws;
    }
}
