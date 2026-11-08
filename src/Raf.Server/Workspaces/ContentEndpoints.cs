using Microsoft.EntityFrameworkCore;
using Raf.Contracts;
using Raf.Domain.Entities;
using Raf.Domain.Requests;
using Raf.Server.Data;
using Raf.Server.Infrastructure;

namespace Raf.Server.Workspaces;

/// <summary>
/// CRUD de los elementos de un workspace. Todas las rutas cuelgan de <c>/workspaces/{ws}</c>:
/// leer requiere Viewer y escribir requiere Editor. Cada consulta filtra por <c>WorkspaceId</c>
/// para que un id de otro workspace nunca sea accesible.
/// </summary>
public static class ContentEndpoints
{
    public static void MapContentEndpoints(this RouteGroupBuilder ws)
    {
        MapEnvironments(ws.MapGroup("/environments").WithTags("Environments"));
        MapVariables(ws.MapGroup("/variables").WithTags("Variables"));
        MapApps(ws.MapGroup("/apps").WithTags("Apps"));
        MapFolders(ws.MapGroup("/folders").WithTags("Folders"));
        MapRequests(ws.MapGroup("/requests").WithTags("Requests"));
        MapSamples(ws.MapGroup("/samples").WithTags("Samples"));
    }

    private static IQueryable<T> In<T>(this DbSet<T> set, Guid ws) where T : SyncEntity =>
        set.Where(x => x.WorkspaceId == ws && !x.IsDeleted);

    private static void SoftDelete(IEnumerable<SyncEntity> entities)
    {
        foreach (var e in entities) e.IsDeleted = true;
    }

    // ---------- Environments ----------

    private static EnvironmentDto ToDto(this WorkspaceEnvironment e) => new(e.Id, e.Name, e.Version, e.ChangeSeq);

    private static void MapEnvironments(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid ws, RafDbContext db, CancellationToken ct) =>
            (await db.Environments.In(ws).OrderBy(e => e.Name).ToListAsync(ct)).Select(e => e.ToDto()));

        g.MapPost("/", async (Guid ws, CreateEnvironmentRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            var env = new WorkspaceEnvironment { Id = req.Id ?? Guid.CreateVersion7(), WorkspaceId = ws, Name = req.Name.Trim() };
            db.Environments.Add(env);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/workspaces/{ws}/environments/{env.Id}", env.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapPut("/{id:guid}", async (Guid ws, Guid id, UpdateEnvironmentRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            var env = await db.Environments.In(ws).FirstOrDefaultAsync(e => e.Id == id, ct);
            if (env is null) return Problems.NotFound("Environment");
            if (env.Version != req.Version) return Problems.VersionConflict(env);

            env.Name = req.Name.Trim();
            await db.SaveChangesAsync(ct);
            return Results.Ok(env.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapDelete("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
        {
            var env = await db.Environments.In(ws).FirstOrDefaultAsync(e => e.Id == id, ct);
            if (env is null) return Problems.NotFound("Environment");

            env.IsDeleted = true;
            SoftDelete(await db.Variables.In(ws).Where(v => v.EnvironmentId == id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireWorkspaceRole(WorkspaceRole.Editor);
    }

    // ---------- Variables ----------

    private static VariableDto ToDto(this Variable v, bool canSeeSecrets) =>
        new(v.Id, v.EnvironmentId, v.Key, v.IsSecret && !canSeeSecrets ? null : v.Value, v.IsSecret, v.Version, v.ChangeSeq);

    private static bool CanSeeSecrets(this HttpContext http) => http.GetRole() >= WorkspaceRole.Editor;

    private static IResult? ValidateKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? Problems.BadRequest("'key' es obligatorio.")
        : key.Length > 200 ? Problems.BadRequest("'key' admite como máximo 200 caracteres.")
        : key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            ? Problems.BadRequest("'key' solo admite letras, números, '_', '.' y '-'.")
        : null;

    private static void MapVariables(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid ws, Guid? environmentId, bool? globalsOnly, HttpContext http, RafDbContext db, CancellationToken ct) =>
        {
            var q = db.Variables.In(ws);
            if (globalsOnly == true) q = q.Where(v => v.EnvironmentId == null);
            else if (environmentId is not null) q = q.Where(v => v.EnvironmentId == environmentId);
            var canSee = http.CanSeeSecrets();
            return (await q.OrderBy(v => v.Key).ToListAsync(ct)).Select(v => v.ToDto(canSee));
        }).WithSummary("Lista variables. Sin filtros devuelve globales y de todos los environments.");

        g.MapPost("/", async (Guid ws, CreateVariableRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (ValidateKey(req.Key) is { } invalid) return invalid;
            if (req.EnvironmentId is { } envId && !await db.Environments.In(ws).AnyAsync(e => e.Id == envId, ct))
                return Problems.BadRequest("El environment no existe.");

            var v = new Variable
            {
                Id = req.Id ?? Guid.CreateVersion7(),
                WorkspaceId = ws,
                EnvironmentId = req.EnvironmentId,
                Key = req.Key.Trim(),
                Value = req.Value ?? "",
                IsSecret = req.IsSecret,
            };
            db.Variables.Add(v);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/workspaces/{ws}/variables/{v.Id}", v.ToDto(canSeeSecrets: true));
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapPut("/{id:guid}", async (Guid ws, Guid id, UpdateVariableRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (ValidateKey(req.Key) is { } invalid) return invalid;
            var v = await db.Variables.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (v is null) return Problems.NotFound("Variable");
            if (v.Version != req.Version) return Problems.VersionConflict(v);

            v.Key = req.Key.Trim();
            v.Value = req.Value ?? "";
            v.IsSecret = req.IsSecret;
            await db.SaveChangesAsync(ct);
            return Results.Ok(v.ToDto(canSeeSecrets: true));
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapDelete("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
        {
            var v = await db.Variables.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (v is null) return Problems.NotFound("Variable");
            v.IsDeleted = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireWorkspaceRole(WorkspaceRole.Editor);
    }

    // ---------- Apps ----------

    private static AppDto ToDto(this App a) => new(a.Id, a.Name, a.Order, a.Version, a.ChangeSeq);

    private static void MapApps(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid ws, RafDbContext db, CancellationToken ct) =>
            (await db.Apps.In(ws).OrderBy(a => a.Order).ThenBy(a => a.Name).ToListAsync(ct)).Select(a => a.ToDto()));

        g.MapGet("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
            await db.Apps.In(ws).FirstOrDefaultAsync(a => a.Id == id, ct) is { } a ? Results.Ok(a.ToDto()) : Problems.NotFound("App"));

        g.MapPost("/", async (Guid ws, CreateAppRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            var a = new App { Id = req.Id ?? Guid.CreateVersion7(), WorkspaceId = ws, Name = req.Name.Trim(), Order = req.Order };
            db.Apps.Add(a);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/workspaces/{ws}/apps/{a.Id}", a.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapPut("/{id:guid}", async (Guid ws, Guid id, UpdateAppRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            var a = await db.Apps.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Problems.NotFound("App");
            if (a.Version != req.Version) return Problems.VersionConflict(a);

            a.Name = req.Name.Trim();
            a.Order = req.Order;
            await db.SaveChangesAsync(ct);
            return Results.Ok(a.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapDelete("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
        {
            var a = await db.Apps.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Problems.NotFound("App");

            a.IsDeleted = true;
            SoftDelete(await db.Folders.In(ws).Where(f => f.AppId == id).ToListAsync(ct));
            var requests = await db.Requests.In(ws).Where(r => r.AppId == id).ToListAsync(ct);
            SoftDelete(requests);
            await SoftDeleteSamplesAsync(db, ws, requests, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireWorkspaceRole(WorkspaceRole.Editor)
          .WithSummary("Elimina la app con todas sus carpetas, requests y samples.");
    }

    private static async Task SoftDeleteSamplesAsync(RafDbContext db, Guid ws, List<ApiRequest> requests, CancellationToken ct)
    {
        var ids = requests.Select(r => r.Id).ToList();
        if (ids.Count > 0) SoftDelete(await db.Samples.In(ws).Where(s => ids.Contains(s.RequestId)).ToListAsync(ct));
    }

    // ---------- Carpetas ----------

    private static FolderDto ToDto(this Folder f) =>
        new(f.Id, f.AppId, f.ParentFolderId, f.Name, f.Order, f.Version, f.ChangeSeq);

    private static void MapFolders(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid ws, Guid? appId, RafDbContext db, CancellationToken ct) =>
        {
            var q = db.Folders.In(ws);
            if (appId is not null) q = q.Where(f => f.AppId == appId);
            return (await q.OrderBy(f => f.Order).ThenBy(f => f.Name).ToListAsync(ct)).Select(f => f.ToDto());
        });

        g.MapPost("/", async (Guid ws, CreateFolderRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            if (!await db.Apps.In(ws).AnyAsync(a => a.Id == req.AppId, ct)) return Problems.BadRequest("La app no existe.");
            if (req.ParentFolderId is { } parentId &&
                !await db.Folders.In(ws).AnyAsync(f => f.Id == parentId && f.AppId == req.AppId, ct))
                return Problems.BadRequest("La carpeta padre no existe en esa app.");

            var f = new Folder
            {
                Id = req.Id ?? Guid.CreateVersion7(), WorkspaceId = ws, AppId = req.AppId,
                ParentFolderId = req.ParentFolderId, Name = req.Name.Trim(), Order = req.Order,
            };
            db.Folders.Add(f);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/workspaces/{ws}/folders/{f.Id}", f.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapPut("/{id:guid}", async (Guid ws, Guid id, UpdateFolderRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            var f = await db.Folders.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return Problems.NotFound("Carpeta");
            if (f.Version != req.Version) return Problems.VersionConflict(f);

            if (req.ParentFolderId != f.ParentFolderId && req.ParentFolderId is { } newParent)
            {
                var folders = await db.Folders.In(ws).Where(x => x.AppId == f.AppId).ToDictionaryAsync(x => x.Id, ct);
                if (!folders.ContainsKey(newParent)) return Problems.BadRequest("La carpeta padre no existe en esa app.");
                for (Guid? cursor = newParent; cursor is { } c; cursor = folders.GetValueOrDefault(c)?.ParentFolderId)
                    if (c == f.Id) return Problems.BadRequest("Una carpeta no puede moverse dentro de sí misma.");
            }

            f.Name = req.Name.Trim();
            f.ParentFolderId = req.ParentFolderId;
            f.Order = req.Order;
            await db.SaveChangesAsync(ct);
            return Results.Ok(f.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapDelete("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
        {
            var f = await db.Folders.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return Problems.NotFound("Carpeta");

            var all = await db.Folders.In(ws).Where(x => x.AppId == f.AppId).ToListAsync(ct);
            var toDelete = new HashSet<Guid> { f.Id };
            for (var added = true; added;)
            {
                added = false;
                foreach (var x in all.Where(x => x.ParentFolderId is { } p && toDelete.Contains(p) && !toDelete.Contains(x.Id)))
                    added |= toDelete.Add(x.Id);
            }

            SoftDelete(all.Where(x => toDelete.Contains(x.Id)));
            var requests = await db.Requests.In(ws).Where(r => r.FolderId != null && toDelete.Contains(r.FolderId.Value)).ToListAsync(ct);
            SoftDelete(requests);
            await SoftDeleteSamplesAsync(db, ws, requests, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireWorkspaceRole(WorkspaceRole.Editor)
          .WithSummary("Elimina la carpeta con sus subcarpetas, requests y samples.");
    }

    // ---------- Requests ----------

    private static RequestDto ToDto(this ApiRequest r) =>
        new(r.Id, r.AppId, r.FolderId, r.Name, r.Order, r.Spec, r.Version, r.ChangeSeq);

    private static IResult? ValidateSpec(RequestSpec? spec) =>
        spec is null ? null
        : string.IsNullOrWhiteSpace(spec.Method) || spec.Method.Length > 20 ? Problems.BadRequest("'method' inválido.")
        : spec.Headers is null || spec.QueryParams is null || spec.Body is null || spec.Auth is null
            ? Problems.BadRequest("'spec' incompleto: headers, queryParams, body y auth son obligatorios.")
        : null;

    private static async Task<bool> FolderBelongsToAppAsync(RafDbContext db, Guid ws, Guid? folderId, Guid appId, CancellationToken ct) =>
        folderId is not { } fid || await db.Folders.In(ws).AnyAsync(f => f.Id == fid && f.AppId == appId, ct);

    private static void MapRequests(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid ws, Guid? appId, RafDbContext db, CancellationToken ct) =>
        {
            var q = db.Requests.In(ws);
            if (appId is not null) q = q.Where(r => r.AppId == appId);
            return (await q.OrderBy(r => r.Order).ThenBy(r => r.Name).ToListAsync(ct)).Select(r => r.ToDto());
        });

        g.MapGet("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
            await db.Requests.In(ws).FirstOrDefaultAsync(r => r.Id == id, ct) is { } r ? Results.Ok(r.ToDto()) : Problems.NotFound("Request"));

        g.MapPost("/", async (Guid ws, CreateRequestRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            if (ValidateSpec(req.Spec) is { } badSpec) return badSpec;
            if (!await db.Apps.In(ws).AnyAsync(a => a.Id == req.AppId, ct)) return Problems.BadRequest("La app no existe.");
            if (!await FolderBelongsToAppAsync(db, ws, req.FolderId, req.AppId, ct))
                return Problems.BadRequest("La carpeta no existe en esa app.");

            var r = new ApiRequest
            {
                Id = req.Id ?? Guid.CreateVersion7(), WorkspaceId = ws, AppId = req.AppId, FolderId = req.FolderId,
                Name = req.Name.Trim(), Order = req.Order, Spec = req.Spec ?? RequestSpec.Empty,
            };
            db.Requests.Add(r);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/workspaces/{ws}/requests/{r.Id}", r.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapPut("/{id:guid}", async (Guid ws, Guid id, UpdateRequestRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            if (req.Spec is null) return Problems.BadRequest("'spec' es obligatorio.");
            if (ValidateSpec(req.Spec) is { } badSpec) return badSpec;
            var r = await db.Requests.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Problems.NotFound("Request");
            if (r.Version != req.Version) return Problems.VersionConflict(r);
            if (!await FolderBelongsToAppAsync(db, ws, req.FolderId, r.AppId, ct))
                return Problems.BadRequest("La carpeta no existe en esa app.");

            r.Name = req.Name.Trim();
            r.FolderId = req.FolderId;
            r.Order = req.Order;
            r.Spec = req.Spec;
            await db.SaveChangesAsync(ct);
            return Results.Ok(r.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapDelete("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
        {
            var r = await db.Requests.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Problems.NotFound("Request");
            r.IsDeleted = true;
            await SoftDeleteSamplesAsync(db, ws, [r], ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireWorkspaceRole(WorkspaceRole.Editor)
          .WithSummary("Elimina el request y sus samples.");
    }

    // ---------- Samples ----------

    private static SampleDto ToDto(this Sample s) =>
        new(s.Id, s.RequestId, s.Name, s.Order, s.Overrides, s.Version, s.ChangeSeq);

    private static void MapSamples(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid ws, Guid? requestId, RafDbContext db, CancellationToken ct) =>
        {
            var q = db.Samples.In(ws);
            if (requestId is not null) q = q.Where(s => s.RequestId == requestId);
            return (await q.OrderBy(s => s.Order).ThenBy(s => s.Name).ToListAsync(ct)).Select(s => s.ToDto());
        });

        g.MapGet("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
            await db.Samples.In(ws).FirstOrDefaultAsync(s => s.Id == id, ct) is { } s ? Results.Ok(s.ToDto()) : Problems.NotFound("Sample"));

        g.MapGet("/{id:guid}/resolved", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
        {
            var sample = await db.Samples.In(ws).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (sample is null) return Problems.NotFound("Sample");
            var parent = await db.Requests.In(ws).FirstAsync(r => r.Id == sample.RequestId, ct);
            return Results.Ok(SampleMerger.Merge(parent.Spec, sample.Overrides));
        }).WithSummary("Request efectivo del sample: el request padre con los overrides aplicados (sin resolver variables).");

        g.MapPost("/", async (Guid ws, CreateSampleRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            if (!await db.Requests.In(ws).AnyAsync(r => r.Id == req.RequestId, ct)) return Problems.BadRequest("El request no existe.");

            var s = new Sample
            {
                Id = req.Id ?? Guid.CreateVersion7(), WorkspaceId = ws, RequestId = req.RequestId,
                Name = req.Name.Trim(), Order = req.Order, Overrides = req.Overrides ?? SampleOverrides.Empty,
            };
            db.Samples.Add(s);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/workspaces/{ws}/samples/{s.Id}", s.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapPut("/{id:guid}", async (Guid ws, Guid id, UpdateSampleRequest req, RafDbContext db, CancellationToken ct) =>
        {
            if (Problems.ValidateName(req.Name) is { } invalid) return invalid;
            var s = await db.Samples.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Problems.NotFound("Sample");
            if (s.Version != req.Version) return Problems.VersionConflict(s);

            s.Name = req.Name.Trim();
            s.Order = req.Order;
            s.Overrides = req.Overrides ?? SampleOverrides.Empty;
            await db.SaveChangesAsync(ct);
            return Results.Ok(s.ToDto());
        }).RequireWorkspaceRole(WorkspaceRole.Editor);

        g.MapDelete("/{id:guid}", async (Guid ws, Guid id, RafDbContext db, CancellationToken ct) =>
        {
            var s = await db.Samples.In(ws).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Problems.NotFound("Sample");
            s.IsDeleted = true;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireWorkspaceRole(WorkspaceRole.Editor);
    }
}
