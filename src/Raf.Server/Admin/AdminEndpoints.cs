using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Raf.Contracts;
using Raf.Domain.Entities;
using Raf.Server.Auth;
using Raf.Server.Data;
using Raf.Server.Infrastructure;

namespace Raf.Server.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").WithTags("Admin").RequireAuthorization(Policies.Admin);

        // ---------- Usuarios ----------

        admin.MapGet("/users", async (RafDbContext db, CancellationToken ct) =>
            (await db.Users.OrderBy(u => u.DisplayName).ToListAsync(ct)).Select(u => u.ToDto()));

        admin.MapPost("/users", async (CreateUserRequest req, UserManager<AppUser> users) =>
        {
            if (Problems.ValidateName(req.DisplayName, "displayName") is { } invalid) return invalid;

            var temporary = PasswordGenerator.Generate();
            var user = new AppUser
            {
                Id = Guid.CreateVersion7(),
                Email = req.Email?.Trim(),
                UserName = req.Email?.Trim(),
                DisplayName = req.DisplayName.Trim(),
                IsAdmin = req.IsAdmin,
                MustChangePassword = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var result = await users.CreateAsync(user, temporary);
            if (!result.Succeeded) return result.ToProblem();

            return Results.Created($"/admin/users/{user.Id}", new CreateUserResponse(user.ToDto(), temporary));
        }).WithSummary("Crea un usuario con contraseña temporal. El admin se la pasa al usuario por fuera de la app.");

        admin.MapPut("/users/{id:guid}", async (
            Guid id, UpdateUserRequest req, UserManager<AppUser> users, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.DisplayName, "displayName") is { } invalid) return invalid;
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Problems.NotFound("Usuario");

            if (user.IsAdmin && !req.IsAdmin && await IsLastActiveAdminAsync(db, user.Id, ct))
                return Problems.Conflict("No se puede quitar el rol Admin al último administrador activo.");

            user.DisplayName = req.DisplayName.Trim();
            user.IsAdmin = req.IsAdmin;
            var email = req.Email?.Trim();
            if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                user.Email = email;
                user.UserName = email;
            }
            var result = await users.UpdateAsync(user);
            return result.Succeeded ? Results.Ok(user.ToDto()) : result.ToProblem();
        });

        admin.MapPost("/users/{id:guid}/reset-password", async (
            Guid id, UserManager<AppUser> users, TokenService tokens, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Problems.NotFound("Usuario");

            var temporary = PasswordGenerator.Generate();
            await users.RemovePasswordAsync(user);
            var result = await users.AddPasswordAsync(user, temporary);
            if (!result.Succeeded) return result.ToProblem();

            user.MustChangePassword = true;
            await users.UpdateAsync(user);
            await users.SetLockoutEndDateAsync(user, null);
            await tokens.RevokeAllAsync(user.Id, ct);
            return Results.Ok(new ResetPasswordResponse(temporary));
        }).WithSummary("Genera una nueva contraseña temporal y cierra las sesiones del usuario.");

        admin.MapPost("/users/{id:guid}/disable", async (
            Guid id, ClaimsPrincipal me, UserManager<AppUser> users, RafDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Problems.NotFound("Usuario");
            if (user.Id == me.GetUserId()) return Problems.Conflict("No podés deshabilitar tu propia cuenta.");
            if (user.IsAdmin && await IsLastActiveAdminAsync(db, user.Id, ct))
                return Problems.Conflict("No se puede deshabilitar al último administrador activo.");

            user.IsDisabled = true;
            await users.UpdateAsync(user);
            await tokens.RevokeAllAsync(user.Id, ct);
            return Results.Ok(user.ToDto());
        });

        admin.MapPost("/users/{id:guid}/enable", async (Guid id, UserManager<AppUser> users) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Problems.NotFound("Usuario");
            user.IsDisabled = false;
            await users.UpdateAsync(user);
            return Results.Ok(user.ToDto());
        });

        // ---------- Workspaces ----------

        admin.MapGet("/workspaces", async (RafDbContext db, CancellationToken ct) =>
        {
            var workspaces = await db.Workspaces.Where(w => !w.IsDeleted).OrderBy(w => w.Name).ToListAsync(ct);
            var ids = workspaces.Select(w => w.Id).ToList();
            var members = await (
                from m in db.WorkspaceMembers
                join u in db.Users on m.UserId equals u.Id
                where ids.Contains(m.WorkspaceId) && !m.IsDeleted
                select new { m.WorkspaceId, m.Role, User = u }).ToListAsync(ct);

            return workspaces.Select(w =>
            {
                var ms = members.Where(m => m.WorkspaceId == w.Id).ToList();
                var owner = ms.FirstOrDefault(m => m.Role == WorkspaceRole.Owner)?.User.ToSummary();
                return new AdminWorkspaceDto(w.Id, w.Name, owner, ms.Count);
            });
        });

        admin.MapPut("/workspaces/{id:guid}/owner", async (Guid id, SetOwnerRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (!await db.Workspaces.AnyAsync(w => w.Id == id && !w.IsDeleted, ct)) return Problems.NotFound("Workspace");
            var user = await db.Users.FindAsync([req.UserId], ct);
            if (user is null || user.IsDisabled) return Problems.BadRequest("El usuario no existe o está deshabilitado.");

            await WorkspaceOwnership.SetOwnerAsync(db, id, req.UserId, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithSummary("Reasigna el Owner de un workspace. El Owner anterior queda como Editor.");
    }

    private static async Task<bool> IsLastActiveAdminAsync(RafDbContext db, Guid userId, CancellationToken ct) =>
        !await db.Users.AnyAsync(u => u.IsAdmin && !u.IsDisabled && u.Id != userId, ct);
}

public static class WorkspaceOwnership
{
    /// <summary>
    /// Deja a <paramref name="newOwnerId"/> como único Owner (agregándolo como miembro si no lo era)
    /// y degrada al Owner anterior a Editor. No guarda cambios.
    /// </summary>
    public static async Task SetOwnerAsync(RafDbContext db, Guid workspaceId, Guid newOwnerId, CancellationToken ct)
    {
        var members = await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).ToListAsync(ct);

        foreach (var m in members.Where(m => m.Role == WorkspaceRole.Owner && m.UserId != newOwnerId))
            m.Role = WorkspaceRole.Editor;

        var target = members.FirstOrDefault(m => m.UserId == newOwnerId);
        if (target is null)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspaceId,
                UserId = newOwnerId,
                Role = WorkspaceRole.Owner,
            });
        }
        else
        {
            target.Role = WorkspaceRole.Owner;
            target.IsDeleted = false;
        }
    }
}
