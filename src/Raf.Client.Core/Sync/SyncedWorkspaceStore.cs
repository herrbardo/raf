using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raf.Client.Core.Api;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Local;
using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Client.Core.Sync;

/// <summary>Un cambio listo para enviarse, con el estado actual del elemento.</summary>
internal sealed record OutboxItem(
    long Seq, Guid WorkspaceId, ChangeKind Kind, Guid EntityId, OutboxOp Op, long BaseVersion, int Revision, object? Dto);

/// <summary>
/// Datos locales de un usuario en un servidor. Toda lectura y edición ocurre acá, sin red: las ediciones se aplican
/// y se encolan, y <see cref="SyncEngine"/> las envía. Una sola conexión, usada de a una operación por vez.
/// </summary>
public sealed class SyncedWorkspaceStore : IWorkspaceStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CacheDb> _options;
    private readonly SecretCipher _cipher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _inFlight;

    private SyncedWorkspaceStore(SqliteConnection connection, SecretCipher cipher)
    {
        _connection = connection;
        _cipher = cipher;
        _options = new DbContextOptionsBuilder<CacheDb>().UseSqlite(connection).Options;
    }

    /// <summary>Hay ediciones nuevas para enviar o cambió lo pendiente.</summary>
    internal event Action? LocalChange;

    /// <summary>Abre (o crea) la caché en <paramref name="path"/>. Un valor cifrado sin su clave se recupera del servidor.</summary>
    internal static async Task<SyncedWorkspaceStore> OpenAsync(string path, ISecureStore secure)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            // secure_delete: un valor secreto borrado de la caché no debe quedar en las páginas libres del archivo.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA secure_delete=ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        var keyName = "cache-key-" + Path.GetFileNameWithoutExtension(path);
        var stored = secure.Read(keyName);
        var key = stored is null ? null : Convert.FromBase64String(stored);
        var keyIsNew = key is null;
        if (key is null)
        {
            key = RandomNumberGenerator.GetBytes(32);
            secure.Write(keyName, Convert.ToBase64String(key));
        }

        var store = new SyncedWorkspaceStore(connection, new SecretCipher(key));
        await using var db = store.NewDb();
        await db.Database.EnsureCreatedAsync();
        if (keyIsNew) await db.Workspaces.ExecuteUpdateAsync(s => s.SetProperty(w => w.LastSeq, 0L));
        return store;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await _connection.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private CacheDb NewDb() => new(_options);

    private async Task<T> RunAsync<T>(Func<CacheDb, Task<T>> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = NewDb();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await work(db);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunAsync(Func<CacheDb, Task> work, CancellationToken ct = default) =>
        RunAsync<object?>(async db => { await work(db); return null; }, ct);

    private async Task<T> EditAsync<T>(Func<CacheDb, Task<T>> work, CancellationToken ct)
    {
        var result = await RunAsync(work, ct);
        LocalChange?.Invoke();
        return result;
    }

    // ---------- Formato en disco ----------

    private string Encode(object dto) => SyncItems.Serialize(
        dto is VariableDto { IsSecret: true, Value: { } value } v ? v with { Value = _cipher.Protect(value) } : dto);

    private object Decode(ChangeKind kind, string json)
    {
        var dto = SyncItems.Deserialize(kind, json);
        return dto is VariableDto { IsSecret: true, Value: { } value } v && value.StartsWith(SecretCipher.Prefix, StringComparison.Ordinal)
            ? v with { Value = _cipher.Unprotect(value) }
            : dto;
    }

    private object Decode(CachedItem row) => Decode(row.Kind, row.Json);

    private void Write(CachedItem row, object dto)
    {
        row.Json = Encode(dto);
        row.Version = SyncItems.VersionOf(dto);
        row.Parents = string.Join(',', SyncItems.ParentsOf(dto));
    }

    private static IEnumerable<Guid> ParentsOf(CachedItem row) =>
        row.Parents.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse);

    private static HashSet<Guid> Descendants(IReadOnlyCollection<CachedItem> rows, Guid root)
    {
        var found = new HashSet<Guid>();
        var frontier = new HashSet<Guid> { root };
        while (frontier.Count > 0)
        {
            var next = rows.Where(r => !found.Contains(r.Id) && ParentsOf(r).Any(frontier.Contains)).Select(r => r.Id).ToHashSet();
            found.UnionWith(next);
            frontier = next;
        }
        return found;
    }

    private static WorkspaceDto ToDto(CachedWorkspace w) => new(w.Id, w.Name, w.Role, w.Version, w.ChangeSeq);

    // ---------- Lectura ----------

    public Task<IReadOnlyList<WorkspaceDto>> GetWorkspacesAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<WorkspaceDto>>(async db =>
            (await db.Workspaces.AsNoTracking().ToListAsync(ct)).OrderBy(w => w.Name).Select(ToDto).ToList(), ct);

    private Task<List<T>> ListAsync<T>(Guid ws, ChangeKind kind, CancellationToken ct) =>
        RunAsync(async db =>
            (await db.Items.AsNoTracking().Where(x => x.WorkspaceId == ws && x.Kind == kind && !x.IsDeleted).ToListAsync(ct))
            .Select(r => (T)Decode(r)).ToList(), ct);

    private Task<T> GetAsync<T>(Guid ws, ChangeKind kind, Guid id, CancellationToken ct) =>
        RunAsync(async db => (T)Decode(await FindAsync(db, ws, kind, id, ct)), ct);

    private static async Task<CachedItem> FindAsync(CacheDb db, Guid ws, ChangeKind kind, Guid id, CancellationToken ct) =>
        await db.Items.FirstOrDefaultAsync(x => x.WorkspaceId == ws && x.Kind == kind && x.Id == id && !x.IsDeleted, ct)
        ?? throw new ApiException(HttpStatusCode.NotFound, CoreTexts.Store_ItemGone);

    public async Task<IReadOnlyList<EnvironmentDto>> GetEnvironmentsAsync(Guid ws, CancellationToken ct = default) =>
        (await ListAsync<EnvironmentDto>(ws, ChangeKind.Environment, ct)).OrderBy(x => x.Name).ToList();

    public async Task<IReadOnlyList<VariableDto>> GetVariablesAsync(Guid ws, CancellationToken ct = default) =>
        (await ListAsync<VariableDto>(ws, ChangeKind.Variable, ct)).OrderBy(x => x.Key).ToList();

    public async Task<IReadOnlyList<AppDto>> GetAppsAsync(Guid ws, CancellationToken ct = default) =>
        (await ListAsync<AppDto>(ws, ChangeKind.App, ct)).OrderBy(x => x.Order).ThenBy(x => x.Name).ToList();

    public async Task<IReadOnlyList<FolderDto>> GetFoldersAsync(Guid ws, CancellationToken ct = default) =>
        (await ListAsync<FolderDto>(ws, ChangeKind.Folder, ct)).OrderBy(x => x.Order).ThenBy(x => x.Name).ToList();

    public async Task<IReadOnlyList<RequestDto>> GetRequestsAsync(Guid ws, CancellationToken ct = default) =>
        (await ListAsync<RequestDto>(ws, ChangeKind.Request, ct)).OrderBy(x => x.Order).ThenBy(x => x.Name).ToList();

    public async Task<IReadOnlyList<SampleDto>> GetSamplesAsync(Guid ws, CancellationToken ct = default) =>
        (await ListAsync<SampleDto>(ws, ChangeKind.Sample, ct)).OrderBy(x => x.Order).ThenBy(x => x.Name).ToList();

    public Task<RequestDto> GetRequestAsync(Guid ws, Guid id, CancellationToken ct = default) =>
        GetAsync<RequestDto>(ws, ChangeKind.Request, id, ct);

    public Task<SampleDto> GetSampleAsync(Guid ws, Guid id, CancellationToken ct = default) =>
        GetAsync<SampleDto>(ws, ChangeKind.Sample, id, ct);

    // ---------- Validación local: sin conexión, el servidor no puede rechazarlo en el momento ----------

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ApiException(HttpStatusCode.BadRequest, CoreTexts.Store_NameRequired);
        if (name.Length > 200) throw new ApiException(HttpStatusCode.BadRequest, CoreTexts.Store_NameTooLong);
    }

    private static void ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ApiException(HttpStatusCode.BadRequest, CoreTexts.Store_KeyRequired);
        if (key.Length > 200) throw new ApiException(HttpStatusCode.BadRequest, CoreTexts.Store_KeyTooLong);
        if (key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')))
            throw new ApiException(HttpStatusCode.BadRequest, CoreTexts.Store_KeyChars);
    }

    private static async Task EnsureCanEditAsync(CacheDb db, Guid ws, CancellationToken ct)
    {
        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == ws, ct)
            ?? throw new ApiException(HttpStatusCode.NotFound, CoreTexts.Store_WorkspaceGone);
        if (workspace.Role < WorkspaceRole.Editor || workspace.AccessLost)
            throw new ApiException(HttpStatusCode.Forbidden, CoreTexts.Store_NoEditPermission);
    }

    // ---------- Edición local ----------

    private Task<T> CreateAsync<T>(Guid ws, ChangeKind kind, T dto, CancellationToken ct) where T : notnull =>
        EditAsync(async db =>
        {
            await EnsureCanEditAsync(db, ws, ct);
            var row = new CachedItem { WorkspaceId = ws, Kind = kind, Id = SyncItems.IdOf(dto) };
            Write(row, dto);
            db.Items.Add(row);
            db.Outbox.Add(new OutboxEntry { WorkspaceId = ws, Kind = kind, EntityId = row.Id, Op = OutboxOp.Create });
            return dto;
        }, ct);

    private Task<T> UpdateAsync<T>(Guid ws, ChangeKind kind, Guid id, long knownVersion, Func<T, T> change, CancellationToken ct)
        where T : notnull =>
        EditAsync(async db =>
        {
            await EnsureCanEditAsync(db, ws, ct);
            var row = await FindAsync(db, ws, kind, id, ct);
            var updated = change((T)Decode(row));
            Write(row, updated);

            // Si quien edita venía de una versión ajena ya superada, se envía esa versión para que el servidor lo detecte.
            var baseVersion = knownVersion >= row.OwnSince ? row.Version : knownVersion;
            var entry = await db.Outbox.FirstOrDefaultAsync(e => e.WorkspaceId == ws && e.Kind == kind && e.EntityId == id, ct);
            if (entry is null)
            {
                db.Outbox.Add(new OutboxEntry { WorkspaceId = ws, Kind = kind, EntityId = id, Op = OutboxOp.Update, BaseVersion = baseVersion });
                return updated;
            }

            entry.Revision++;
            if (entry.Op != OutboxOp.Update) return updated;

            entry.BaseVersion = Math.Min(entry.BaseVersion, baseVersion);
            if (entry.Seq != _inFlight && entry.State == OutboxState.Pending)
                await MoveToTailAsync(db, entry, ct);
            return updated;
        }, ct);

    /// <summary>Una edición puede mover el elemento a un padre creado después: debe enviarse tras él.</summary>
    private static async Task MoveToTailAsync(CacheDb db, OutboxEntry entry, CancellationToken ct)
    {
        db.Outbox.Remove(entry);
        await db.SaveChangesAsync(ct);
        db.Outbox.Add(new OutboxEntry
        {
            WorkspaceId = entry.WorkspaceId, Kind = entry.Kind, EntityId = entry.EntityId, Op = entry.Op,
            BaseVersion = entry.BaseVersion, Revision = entry.Revision,
        });
    }

    private Task DeleteAsync(Guid ws, ChangeKind kind, Guid id, CancellationToken ct) =>
        EditAsync<object?>(async db =>
        {
            await EnsureCanEditAsync(db, ws, ct);
            var rows = await db.Items.Where(x => x.WorkspaceId == ws).ToListAsync(ct);
            var target = rows.FirstOrDefault(r => r.Kind == kind && r.Id == id && !r.IsDeleted)
                ?? throw new ApiException(HttpStatusCode.NotFound, CoreTexts.Store_ItemGone);

            var below = Descendants(rows, id);
            var entries = await db.Outbox.Where(e => e.WorkspaceId == ws).ToListAsync(ct);

            // El borrado del padre arrastra a los hijos en el servidor: sus cambios pendientes ya no se envían.
            db.Outbox.RemoveRange(entries.Where(e => below.Contains(e.EntityId) && e.Seq != _inFlight));

            var own = entries.FirstOrDefault(e => e.Kind == kind && e.EntityId == id);
            var neverSent = own is { Op: OutboxOp.Create } && own.Seq != _inFlight;

            foreach (var row in rows.Where(r => r == target || below.Contains(r.Id)))
            {
                if (neverSent || row.Kind == ChangeKind.Variable) db.Items.Remove(row);
                else row.IsDeleted = true;
            }

            if (neverSent)
            {
                db.Outbox.Remove(own!);
            }
            else if (own is not null && own.Seq == _inFlight)
            {
                own.Op = OutboxOp.Delete;
                own.Revision++;
                own.State = OutboxState.Pending;
            }
            else
            {
                if (own is not null)
                {
                    db.Outbox.Remove(own);
                    await db.SaveChangesAsync(ct);
                }
                db.Outbox.Add(new OutboxEntry { WorkspaceId = ws, Kind = kind, EntityId = id, Op = OutboxOp.Delete });
            }
            return null;
        }, ct);

    public Task<WorkspaceDto> CreateWorkspaceAsync(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        return EditAsync(async db =>
        {
            var workspace = new CachedWorkspace { Id = Guid.CreateVersion7(), Name = name.Trim(), Role = WorkspaceRole.Owner };
            db.Workspaces.Add(workspace);
            db.Outbox.Add(new OutboxEntry
            {
                WorkspaceId = workspace.Id, Kind = ChangeKind.Workspace, EntityId = workspace.Id, Op = OutboxOp.Create,
            });
            return ToDto(workspace);
        }, ct);
    }

    public Task<EnvironmentDto> CreateEnvironmentAsync(Guid ws, string name, Guid? id = null, CancellationToken ct = default)
    {
        ValidateName(name);
        return CreateAsync(ws, ChangeKind.Environment, new EnvironmentDto(id ?? Guid.CreateVersion7(), name.Trim(), 0, 0), ct);
    }

    public Task<EnvironmentDto> UpdateEnvironmentAsync(Guid ws, Guid id, UpdateEnvironmentRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        return UpdateAsync<EnvironmentDto>(ws, ChangeKind.Environment, id, req.Version, x => x with { Name = req.Name.Trim() }, ct);
    }

    public Task DeleteEnvironmentAsync(Guid ws, Guid id, CancellationToken ct = default) => DeleteAsync(ws, ChangeKind.Environment, id, ct);

    public Task<VariableDto> CreateVariableAsync(Guid ws, CreateVariableRequest req, CancellationToken ct = default)
    {
        ValidateKey(req.Key);
        return CreateAsync(ws, ChangeKind.Variable, new VariableDto(
            req.Id ?? Guid.CreateVersion7(), req.EnvironmentId, req.Key.Trim(), req.Value, req.IsSecret, 0, 0), ct);
    }

    public Task<VariableDto> UpdateVariableAsync(Guid ws, Guid id, UpdateVariableRequest req, CancellationToken ct = default)
    {
        ValidateKey(req.Key);
        return UpdateAsync<VariableDto>(ws, ChangeKind.Variable, id, req.Version,
            x => x with { Key = req.Key.Trim(), Value = req.Value, IsSecret = req.IsSecret }, ct);
    }

    public Task DeleteVariableAsync(Guid ws, Guid id, CancellationToken ct = default) => DeleteAsync(ws, ChangeKind.Variable, id, ct);

    public Task<AppDto> CreateAppAsync(Guid ws, CreateAppRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        ValidateAppAuth(req.Auth, req.Login);
        return CreateAsync(ws, ChangeKind.App, new AppDto(
            req.Id ?? Guid.CreateVersion7(), req.Name.Trim(), req.Order, 0, 0, req.Auth ?? Raf.Domain.Requests.AuthSpec.None, req.Login), ct);
    }

    public Task<AppDto> UpdateAppAsync(Guid ws, Guid id, UpdateAppRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        ValidateAppAuth(req.Auth, req.Login);
        return UpdateAsync<AppDto>(ws, ChangeKind.App, id, req.Version,
            x => x with { Name = req.Name.Trim(), Order = req.Order, Auth = req.Auth ?? x.Auth, Login = req.Login ?? x.Login }, ct);
    }

    /// <summary>Las mismas reglas que aplica el servidor, para no encolar un cambio que va a rechazar.</summary>
    private static void ValidateAppAuth(Raf.Domain.Requests.AuthSpec? auth, Raf.Domain.Requests.AppLogin? login)
    {
        if (auth is { Type: Raf.Domain.Requests.AuthType.Inherit }) throw new ApiException(HttpStatusCode.BadRequest, CoreTexts.Store_AppCannotInherit);
        if (login?.Problem() is { } problem && problem != Raf.Domain.Requests.AppLoginProblem.None)
            throw new ApiException(HttpStatusCode.BadRequest, AppLoginTexts.Describe(problem));
    }

    public Task DeleteAppAsync(Guid ws, Guid id, CancellationToken ct = default) => DeleteAsync(ws, ChangeKind.App, id, ct);

    public Task<FolderDto> CreateFolderAsync(Guid ws, CreateFolderRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        return CreateAsync(ws, ChangeKind.Folder, new FolderDto(
            req.Id ?? Guid.CreateVersion7(), req.AppId, req.ParentFolderId, req.Name.Trim(), req.Order, 0, 0), ct);
    }

    public Task<FolderDto> UpdateFolderAsync(Guid ws, Guid id, UpdateFolderRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        return UpdateAsync<FolderDto>(ws, ChangeKind.Folder, id, req.Version,
            x => x with { Name = req.Name.Trim(), ParentFolderId = req.ParentFolderId, Order = req.Order }, ct);
    }

    public Task DeleteFolderAsync(Guid ws, Guid id, CancellationToken ct = default) => DeleteAsync(ws, ChangeKind.Folder, id, ct);

    public Task<RequestDto> CreateRequestAsync(Guid ws, CreateRequestRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        return CreateAsync(ws, ChangeKind.Request, new RequestDto(
            req.Id ?? Guid.CreateVersion7(), req.AppId, req.FolderId, req.Name.Trim(), req.Order,
            req.Spec ?? Raf.Domain.Requests.RequestSpec.Empty, 0, 0), ct);
    }

    public Task<RequestDto> UpdateRequestAsync(Guid ws, Guid id, UpdateRequestRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        return UpdateAsync<RequestDto>(ws, ChangeKind.Request, id, req.Version,
            x => x with { Name = req.Name.Trim(), FolderId = req.FolderId, Order = req.Order, Spec = req.Spec }, ct);
    }

    public Task DeleteRequestAsync(Guid ws, Guid id, CancellationToken ct = default) => DeleteAsync(ws, ChangeKind.Request, id, ct);

    public Task<SampleDto> CreateSampleAsync(Guid ws, CreateSampleRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        return CreateAsync(ws, ChangeKind.Sample, new SampleDto(
            req.Id ?? Guid.CreateVersion7(), req.RequestId, req.Name.Trim(), req.Order,
            req.Overrides ?? Raf.Domain.Requests.SampleOverrides.Empty, 0, 0), ct);
    }

    public Task<SampleDto> UpdateSampleAsync(Guid ws, Guid id, UpdateSampleRequest req, CancellationToken ct = default)
    {
        ValidateName(req.Name);
        return UpdateAsync<SampleDto>(ws, ChangeKind.Sample, id, req.Version,
            x => x with { Name = req.Name.Trim(), Order = req.Order, Overrides = req.Overrides }, ct);
    }

    public Task DeleteSampleAsync(Guid ws, Guid id, CancellationToken ct = default) => DeleteAsync(ws, ChangeKind.Sample, id, ct);

    // ---------- Conflictos ----------

    public Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<SyncConflict>>(async db =>
        {
            var entries = await db.Outbox.AsNoTracking().Where(e => e.State == OutboxState.Conflict).OrderBy(e => e.Seq).ToListAsync(ct);
            var result = new List<SyncConflict>();
            foreach (var e in entries)
            {
                var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == e.WorkspaceId, ct);
                var row = await db.Items.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WorkspaceId == e.WorkspaceId && x.Kind == e.Kind && x.Id == e.EntityId, ct);
                var local = row is null ? null : Decode(row);
                result.Add(new SyncConflict(
                    e.Seq, e.WorkspaceId, workspace?.Name ?? "", e.Kind, e.EntityId,
                    local is null ? workspace?.Name ?? "" : SyncItems.NameOf(local),
                    e.Reason ?? ConflictReason.Rejected, e.Message ?? "",
                    local is null ? "" : SyncItems.ForDisplay(local),
                    e.RemoteJson is null ? null : SyncItems.ForDisplay(Decode(e.Kind, e.RemoteJson))));
            }
            return result;
        }, ct);

    private static async Task<OutboxEntry> ConflictAsync(CacheDb db, long id, CancellationToken ct) =>
        await db.Outbox.FirstOrDefaultAsync(e => e.Seq == id && e.State == OutboxState.Conflict, ct)
        ?? throw new ApiException(HttpStatusCode.NotFound, CoreTexts.Store_ConflictResolved);

    private void Requeue(OutboxEntry entry, CachedItem row)
    {
        if (entry.Reason == ConflictReason.Version && entry.RemoteJson is { } remote)
        {
            // Se envía sobre la versión del servidor que el usuario ya vio.
            var serverVersion = SyncItems.VersionOf(SyncItems.Deserialize(entry.Kind, remote));
            entry.BaseVersion = serverVersion;
            Write(row, SyncItems.WithMeta(Decode(row), serverVersion, 0));
            row.OwnSince = serverVersion;
        }
        entry.State = OutboxState.Pending;
        entry.Reason = null;
        entry.Message = null;
        entry.RemoteJson = null;
        entry.Revision++;
    }

    /// <summary>Se impone la versión local.</summary>
    public Task UseLocalAsync(long conflictId, CancellationToken ct = default) =>
        EditAsync<object?>(async db =>
        {
            var entry = await ConflictAsync(db, conflictId, ct);
            Requeue(entry, await FindAsync(db, entry.WorkspaceId, entry.Kind, entry.EntityId, ct));
            return null;
        }, ct);

    /// <summary>Se reemplaza la versión local por <paramref name="json"/> (el formato de la pantalla de conflictos) y se envía.</summary>
    public Task EditAsync(long conflictId, string json, CancellationToken ct = default) =>
        EditAsync<object?>(async db =>
        {
            var entry = await ConflictAsync(db, conflictId, ct);
            var row = await FindAsync(db, entry.WorkspaceId, entry.Kind, entry.EntityId, ct);
            var current = Decode(row);

            object edited;
            try
            {
                edited = SyncItems.Deserialize(entry.Kind, json);
            }
            catch (JsonException)
            {
                throw new ApiException(HttpStatusCode.BadRequest, CoreTexts.Store_EditedNotJson);
            }

            var merged = SyncItems.KeepIdentity(edited, current);
            if (merged is VariableDto v) ValidateKey(v.Key);
            else ValidateName(SyncItems.NameOf(merged));
            Write(row, merged);
            Requeue(entry, row);
            return null;
        }, ct);

    /// <summary>
    /// Descarta el cambio local. Lo que nunca llegó al servidor se elimina junto con lo creado adentro; lo demás vuelve
    /// a su estado del servidor en la próxima sincronización.
    /// </summary>
    public Task DiscardAsync(long conflictId, CancellationToken ct = default) =>
        EditAsync<object?>(async db =>
        {
            var entry = await ConflictAsync(db, conflictId, ct);
            if (entry.Op == OutboxOp.Create && entry.Kind == ChangeKind.Workspace)
            {
                await ForgetCoreAsync(db, entry.WorkspaceId, ct);
                return null;
            }

            db.Outbox.Remove(entry);
            if (entry.Op == OutboxOp.Create)
            {
                var rows = await db.Items.Where(x => x.WorkspaceId == entry.WorkspaceId).ToListAsync(ct);
                var below = Descendants(rows, entry.EntityId);
                below.Add(entry.EntityId);
                db.Items.RemoveRange(rows.Where(r => below.Contains(r.Id)));
                db.Outbox.RemoveRange(await db.Outbox.Where(
                    e => e.WorkspaceId == entry.WorkspaceId && below.Contains(e.EntityId) && e.Seq != entry.Seq).ToListAsync(ct));
                return null;
            }

            var workspace = await db.Workspaces.FirstAsync(w => w.Id == entry.WorkspaceId, ct);
            workspace.LastSeq = 0;
            return null;
        }, ct);

    /// <summary>
    /// Vuelve a crear el elemento con un id nuevo, junto con los padres eliminados: un id borrado en el servidor no se
    /// puede reutilizar.
    /// </summary>
    public Task RecreateAsync(long conflictId, CancellationToken ct = default) =>
        EditAsync<object?>(async db =>
        {
            var entry = await ConflictAsync(db, conflictId, ct);
            var row = await FindAnyAsync(db, entry.WorkspaceId, entry.EntityId, ct)
                ?? throw new ApiException(HttpStatusCode.Conflict, CoreTexts.Store_NoLocalData);

            var dto = Decode(row);
            var map = new Dictionary<Guid, Guid>();
            foreach (var parent in ParentsOf(row)) map[parent] = await ReviveAsync(db, entry.WorkspaceId, parent, ct);

            db.Outbox.Remove(entry);
            await AddRecreatedAsync(db, row, dto, map, ct);
            return null;
        }, ct);

    private static Task<CachedItem?> FindAnyAsync(CacheDb db, Guid ws, Guid id, CancellationToken ct) =>
        db.Items.FirstOrDefaultAsync(x => x.WorkspaceId == ws && x.Id == id, ct);

    private async Task<Guid> ReviveAsync(CacheDb db, Guid ws, Guid id, CancellationToken ct)
    {
        var row = await FindAnyAsync(db, ws, id, ct)
            ?? throw new ApiException(HttpStatusCode.Conflict, CoreTexts.Store_NoLocalParent);
        if (!row.IsDeleted) return id;
        if (row.ReplacedBy is { } replacement && await FindAnyAsync(db, ws, replacement, ct) is { IsDeleted: false }) return replacement;

        var dto = Decode(row);
        var map = new Dictionary<Guid, Guid>();
        foreach (var parent in ParentsOf(row)) map[parent] = await ReviveAsync(db, ws, parent, ct);
        return await AddRecreatedAsync(db, row, dto, map, ct);
    }

    private async Task<Guid> AddRecreatedAsync(
        CacheDb db, CachedItem old, object dto, IReadOnlyDictionary<Guid, Guid> map, CancellationToken ct)
    {
        var newId = Guid.CreateVersion7();
        var copy = new CachedItem { WorkspaceId = old.WorkspaceId, Kind = old.Kind, Id = newId };
        Write(copy, SyncItems.WithMeta(SyncItems.Remap(dto, newId, map), 0, 0));
        db.Items.Add(copy);
        old.IsDeleted = true;
        old.ReplacedBy = newId;
        db.Outbox.Add(new OutboxEntry { WorkspaceId = old.WorkspaceId, Kind = old.Kind, EntityId = newId, Op = OutboxOp.Create });

        // Se confirma de a uno para que los padres reciban un número de orden menor que sus hijos.
        await db.SaveChangesAsync(ct);
        return newId;
    }

    /// <summary>Borra todo lo local de un workspace al que ya no se tiene acceso.</summary>
    public Task ForgetWorkspaceAsync(Guid ws, CancellationToken ct = default) =>
        EditAsync<object?>(async db =>
        {
            await ForgetCoreAsync(db, ws, ct);
            return null;
        }, ct);

    private static async Task ForgetCoreAsync(CacheDb db, Guid ws, CancellationToken ct)
    {
        await db.Items.Where(x => x.WorkspaceId == ws).ExecuteDeleteAsync(ct);
        await db.Outbox.Where(e => e.WorkspaceId == ws).ExecuteDeleteAsync(ct);
        await db.Workspaces.Where(w => w.Id == ws).ExecuteDeleteAsync(ct);
    }

    // ---------- Para el motor de sincronización ----------

    internal Task<(int Pending, int Conflicts)> CountOutboxAsync() =>
        RunAsync(async db => (
            await db.Outbox.CountAsync(e => e.State == OutboxState.Pending),
            await db.Outbox.CountAsync(e => e.State == OutboxState.Conflict)));

    internal Task<List<CachedWorkspace>> GetWorkspacesForSyncAsync(CancellationToken ct) =>
        RunAsync(db => db.Workspaces.AsNoTracking().Where(w => !w.AccessLost).ToListAsync(ct), ct);

    /// <summary>Compara con la lista del servidor. Devuelve si hubo novedades y a qué workspaces se perdió el acceso.</summary>
    internal Task<(bool Changed, List<WorkspaceLost> Lost)> ApplyWorkspaceListAsync(IReadOnlyList<WorkspaceDto> server, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var changed = false;
            var locals = await db.Workspaces.ToListAsync(ct);
            foreach (var s in server)
            {
                var local = locals.FirstOrDefault(w => w.Id == s.Id);
                if (local is null)
                {
                    db.Workspaces.Add(new CachedWorkspace
                    {
                        Id = s.Id, Name = s.Name, Role = s.MyRole, Version = s.Version, ChangeSeq = s.ChangeSeq,
                    });
                    changed = true;
                    continue;
                }

                changed |= local.Name != s.Name || local.AccessLost;
                local.Name = s.Name;
                local.Version = s.Version;
                local.ChangeSeq = s.ChangeSeq;
                local.AccessLost = false;
            }

            var lost = new List<WorkspaceLost>();
            var entries = await db.Outbox.AsNoTracking().ToListAsync(ct);
            foreach (var local in locals.Where(w => server.All(s => s.Id != w.Id)))
            {
                if (entries.Any(e => e.WorkspaceId == local.Id && e.Kind == ChangeKind.Workspace)) continue; // aún no se subió

                var pending = entries.Count(e => e.WorkspaceId == local.Id);
                lost.Add(new WorkspaceLost(local.Id, local.Name, pending));
                changed = true;
                if (pending > 0) local.AccessLost = true;
                else await ForgetCoreAsync(db, local.Id, ct);
            }
            return (changed, lost);
        }, ct);

    /// <summary>El usuario pasó a Viewer: los valores secretos no pueden seguir en disco.</summary>
    internal Task WipeSecretsAsync(Guid ws, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var pending = await db.Outbox.Where(e => e.WorkspaceId == ws && e.Kind == ChangeKind.Variable).ToListAsync(ct);
            db.Outbox.RemoveRange(pending);

            var rows = await db.Items.Where(x => x.WorkspaceId == ws && x.Kind == ChangeKind.Variable).ToListAsync(ct);
            foreach (var row in rows)
            {
                var dto = (VariableDto)Decode(row);
                if (dto.IsSecret) Write(row, dto with { Value = null });
            }
        }, ct);

    /// <summary>Reemplaza todas las variables y fija el rol, de forma atómica: si falla, se reintenta el cambio de rol.</summary>
    internal Task ReplaceVariablesAsync(Guid ws, WorkspaceRole role, IReadOnlyList<VariableDto>? variables, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var workspace = await db.Workspaces.FirstAsync(w => w.Id == ws, ct);
            workspace.Role = role;
            if (variables is null) return;

            var pending = (await db.Outbox.Where(e => e.WorkspaceId == ws).Select(e => e.EntityId).ToListAsync(ct)).ToHashSet();
            var rows = await db.Items.Where(x => x.WorkspaceId == ws && x.Kind == ChangeKind.Variable).ToListAsync(ct);
            db.Items.RemoveRange(rows.Where(r => !pending.Contains(r.Id) && variables.All(v => v.Id != r.Id)));
            foreach (var v in variables)
            {
                if (pending.Contains(v.Id)) continue;
                var row = rows.FirstOrDefault(r => r.Id == v.Id);
                if (row is null)
                {
                    row = new CachedItem { WorkspaceId = ws, Kind = ChangeKind.Variable, Id = v.Id, OwnSince = v.Version };
                    db.Items.Add(row);
                }
                Write(row, v);
            }
        }, ct);

    /// <summary>Aplica una página del feed. Lo que tiene cambios pendientes no se toca: se resuelve al enviarlo.</summary>
    internal Task<bool> ApplyChangesAsync(Guid ws, ChangesResponse page, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var workspace = await db.Workspaces.FirstAsync(w => w.Id == ws, ct);
            var pending = (await db.Outbox.Where(e => e.WorkspaceId == ws).Select(e => e.EntityId).ToListAsync(ct)).ToHashSet();
            var rows = (await db.Items.Where(x => x.WorkspaceId == ws).ToListAsync(ct)).ToDictionary(r => (r.Kind, r.Id));
            var changed = false;

            void Upsert(ChangeKind kind, IEnumerable<object> dtos)
            {
                foreach (var dto in dtos)
                {
                    var id = SyncItems.IdOf(dto);
                    if (pending.Contains(id)) continue;

                    var version = SyncItems.VersionOf(dto);
                    if (!rows.TryGetValue((kind, id), out var row))
                    {
                        row = new CachedItem { WorkspaceId = ws, Kind = kind, Id = id, OwnSince = version };
                        db.Items.Add(row);
                        rows[(kind, id)] = row;
                        changed = true;
                    }
                    else if (row.Version != version || row.IsDeleted)
                    {
                        row.OwnSince = version;
                        changed = true;
                    }

                    row.IsDeleted = false;
                    Write(row, dto);
                }
            }

            Upsert(ChangeKind.Member, page.Members);
            Upsert(ChangeKind.Environment, page.Environments);
            Upsert(ChangeKind.Variable, page.Variables);
            Upsert(ChangeKind.App, page.Apps);
            Upsert(ChangeKind.Folder, page.Folders);
            Upsert(ChangeKind.Request, page.Requests);
            Upsert(ChangeKind.Sample, page.Samples);

            foreach (var gone in page.Deleted)
            {
                if (pending.Contains(gone.Id) || !rows.TryGetValue((gone.Kind, gone.Id), out var row) || row.IsDeleted) continue;
                changed = true;
                if (row.Kind is ChangeKind.Variable or ChangeKind.Member)
                {
                    db.Items.Remove(row);
                    rows.Remove((gone.Kind, gone.Id));
                }
                else
                {
                    row.IsDeleted = true;
                }
            }

            if (page.Workspace is { } w)
            {
                changed |= workspace.Name != w.Name;
                workspace.Name = w.Name;
                workspace.Version = w.Version;
                workspace.ChangeSeq = w.ChangeSeq;
            }
            workspace.LastSeq = page.LastSeq;
            return changed;
        }, ct);

    /// <summary>Los borrados locales solo se conservan mientras queden cambios por resolver en el workspace.</summary>
    internal Task PurgeDeletedAsync(Guid ws, CancellationToken ct) =>
        RunAsync(async db =>
        {
            if (!await db.Outbox.AnyAsync(e => e.WorkspaceId == ws, ct))
                await db.Items.Where(x => x.WorkspaceId == ws && x.IsDeleted).ExecuteDeleteAsync(ct);
        }, ct);

    /// <summary>
    /// El próximo cambio enviable, o null. Se salta lo que tiene un conflicto sin resolver y lo que depende de ello;
    /// el resto de la bandeja sigue su curso. Queda marcado como "en vuelo" hasta que se confirme o falle.
    /// </summary>
    internal Task<OutboxItem?> TakeNextAsync(Func<OutboxEntry, bool> filter, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var entries = (await db.Outbox.AsNoTracking().OrderBy(e => e.Seq).ToListAsync(ct)).Where(filter).ToList();
            var lost = (await db.Workspaces.AsNoTracking().Where(w => w.AccessLost).Select(w => w.Id).ToListAsync(ct)).ToHashSet();
            var blocked = new HashSet<Guid>(entries.Where(e => e.State == OutboxState.Conflict).Select(e => e.EntityId));

            foreach (var entry in entries.Where(e => e.State == OutboxState.Pending && !lost.Contains(e.WorkspaceId)))
            {
                if (blocked.Contains(entry.WorkspaceId) && entry.Kind != ChangeKind.Workspace) continue;

                object? dto = null;
                if (entry.Kind == ChangeKind.Workspace)
                {
                    dto = ToDto(await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == entry.EntityId, ct));
                }
                else
                {
                    var row = await db.Items.AsNoTracking().FirstOrDefaultAsync(
                        x => x.WorkspaceId == entry.WorkspaceId && x.Kind == entry.Kind && x.Id == entry.EntityId, ct);
                    if (row is not null)
                    {
                        if (ParentsOf(row).Any(blocked.Contains))
                        {
                            blocked.Add(entry.EntityId);
                            continue;
                        }
                        if (entry.Op != OutboxOp.Delete) dto = Decode(row);
                    }

                    if (dto is null && entry.Op != OutboxOp.Delete)
                    {
                        await db.Outbox.Where(e => e.Seq == entry.Seq).ExecuteDeleteAsync(ct); // sin datos no hay nada que enviar
                        continue;
                    }
                }

                _inFlight = entry.Seq;
                return new OutboxItem(entry.Seq, entry.WorkspaceId, entry.Kind, entry.EntityId, entry.Op, entry.BaseVersion, entry.Revision, dto);
            }
            return null;
        }, ct);

    /// <summary>Se interrumpió el envío (sin conexión): sigue pendiente.</summary>
    internal async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        _inFlight = null;
        _gate.Release();
    }

    /// <summary>El servidor aceptó el cambio. Si se editó mientras viajaba, la edición queda pendiente sobre la versión nueva.</summary>
    internal async Task AckAsync(OutboxItem item, object? serverDto)
    {
        await RunAsync(async db =>
        {
            _inFlight = null;
            var entry = await db.Outbox.FirstOrDefaultAsync(e => e.Seq == item.Seq);

            if (item.Kind == ChangeKind.Workspace)
            {
                if (entry is not null) db.Outbox.Remove(entry);
                if (serverDto is WorkspaceDto w && await db.Workspaces.FirstOrDefaultAsync(x => x.Id == w.Id) is { } workspace)
                {
                    workspace.Version = w.Version;
                    workspace.ChangeSeq = w.ChangeSeq;
                }
                return;
            }

            var row = await db.Items.FirstOrDefaultAsync(x => x.WorkspaceId == item.WorkspaceId && x.Kind == item.Kind && x.Id == item.EntityId);
            var unchanged = entry is null || entry.Revision == item.Revision;

            if (unchanged)
            {
                if (entry is not null) db.Outbox.Remove(entry);
                if (row is not null && serverDto is not null) Write(row, serverDto);
                return;
            }

            if (serverDto is null) return;
            var version = SyncItems.VersionOf(serverDto);
            if (row is not null) Write(row, SyncItems.WithMeta(Decode(row), version, 0));
            entry!.BaseVersion = version;
            if (entry.Op == OutboxOp.Create) entry.Op = OutboxOp.Update;
        });
    }

    /// <summary>El servidor rechazó el cambio. Si mientras tanto se borró el elemento, ese borrado sigue su curso.</summary>
    internal Task MarkConflictAsync(OutboxItem item, ConflictReason reason, string message, object? remote) =>
        RunAsync(async db =>
        {
            _inFlight = null;
            var entry = await db.Outbox.FirstOrDefaultAsync(e => e.Seq == item.Seq);
            if (entry is null || entry.Op != item.Op) return;

            entry.State = OutboxState.Conflict;
            entry.Reason = reason;
            entry.Message = message;
            entry.RemoteJson = remote is null ? null : Encode(remote);
        });

    /// <summary>¿Falta alguno de los padres de este elemento?</summary>
    internal Task<bool> HasMissingParentAsync(OutboxItem item) =>
        RunAsync(async db =>
        {
            IReadOnlyList<Guid> parents = item.Dto is null ? [] : SyncItems.ParentsOf(item.Dto);
            foreach (var id in parents)
                if (await FindAnyAsync(db, item.WorkspaceId, id, default) is null or { IsDeleted: true })
                    return true;
            return false;
        });
}
