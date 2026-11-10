using System.Diagnostics;
using System.Net;
using Raf.Client.Core.Api;
using Raf.Client.Core.Local;
using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Client.Core.Sync;

/// <summary>
/// Trae lo nuevo del servidor y envía la bandeja de salida. Corre un ciclo por vez, disparado por una edición, un
/// aviso, la recuperación del foco o el paso del tiempo. Los fallos de red solo posponen el trabajo.
/// </summary>
public sealed class SyncEngine
{
    private const int PageSize = 500;
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(15);

    private readonly SyncedWorkspaceStore _store;
    private readonly ApiWorkspaceStore _api;
    private readonly IChangeSubscriber _subscriber;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly HashSet<Guid> _announcedLost = [];
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private volatile bool _offline;

    internal SyncEngine(SyncedWorkspaceStore store, ApiWorkspaceStore api, IChangeSubscriber subscriber, string lockPath)
    {
        _store = store;
        _api = api;
        _subscriber = subscriber;
        _lockPath = lockPath;
        _subscriber.ChangeAvailable += Kick;
        _store.LocalChange += OnLocalChange;
    }

    public event Action<SyncStatus>? StatusChanged;

    /// <summary>Llegaron datos nuevos del servidor.</summary>
    public event Action? DataChanged;

    public event Action<WorkspaceLost>? WorkspaceLost;

    public void Start()
    {
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        _subscriber.Start();
        Kick();
    }

    public async Task StopAsync()
    {
        _subscriber.ChangeAvailable -= Kick;
        _store.LocalChange -= OnLocalChange;
        _subscriber.Dispose();
        if (_stop is null) return;
        await _stop.CancelAsync();
        if (_loop is not null) await _loop;
    }

    /// <summary>Pide un ciclo lo antes posible. Varios pedidos seguidos se atienden con uno solo.</summary>
    public void Kick()
    {
        try
        {
            _kick.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ya hay un ciclo pedido.
        }
    }

    private void OnLocalChange()
    {
        _ = PublishStatusAsync();
        Kick();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _kick.WaitAsync(_offline ? OfflineRetry : IdleInterval, ct);
                await SyncOnceAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre normal.
        }
    }

    public async Task<SyncStatus> GetStatusAsync()
    {
        var (pending, conflicts) = await _store.CountOutboxAsync();
        var state = conflicts > 0 ? SyncState.Conflict
            : _offline ? SyncState.Offline
            : pending > 0 ? SyncState.Pending
            : SyncState.Synced;
        return new SyncStatus(state, pending, conflicts);
    }

    private async Task PublishStatusAsync()
    {
        try
        {
            var status = await GetStatusAsync();
            StatusChanged?.Invoke(status);
        }
        catch (ObjectDisposedException)
        {
            // La sesión se cerró mientras tanto.
        }
    }

    /// <summary>Un ciclo completo. Devuelve false si no se pudo terminar por falta de conexión.</summary>
    public async Task<bool> SyncOnceAsync(CancellationToken ct = default)
    {
        await _cycle.WaitAsync(ct);
        try
        {
            using var instanceLock = TryAcquireInstanceLock();
            if (instanceLock is null) return true; // otra ventana de la app ya está sincronizando esta caché

            await RunCycleAsync(ct);
            _offline = false;
            return true;
        }
        catch (SessionExpiredException)
        {
            return false; // SessionManager ya avisó: la app vuelve al login
        }
        catch (ApiException)
        {
            _offline = true;
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Un fallo inesperado no debe matar el motor: se reintenta en el próximo ciclo.
            Debug.WriteLine($"Error de sincronización: {e}");
            _offline = true;
            return false;
        }
        finally
        {
            _cycle.Release();
            if (!ct.IsCancellationRequested) await PublishStatusAsync();
        }
    }

    private FileStream? TryAcquireInstanceLock()
    {
        try
        {
            return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        // Un workspace creado sin conexión debe existir en el servidor antes de preguntar por él.
        await PushAsync(e => e.Kind == ChangeKind.Workspace, ct);

        var (listChanged, lost) = await _store.ApplyWorkspaceListAsync(await _api.GetWorkspacesAsync(ct), ct);
        foreach (var item in lost.Where(l => l.PendingChanges == 0 || _announcedLost.Add(l.Id))) WorkspaceLost?.Invoke(item);

        var changed = listChanged;
        var workspaces = await _store.GetWorkspacesForSyncAsync(ct);
        _subscriber.Watch(workspaces.Select(w => w.Id).ToList());
        foreach (var workspace in workspaces)
            changed |= await PullAsync(workspace, ct);
        if (changed) DataChanged?.Invoke();

        await PushAsync(_ => true, ct);

        foreach (var workspace in await _store.GetWorkspacesForSyncAsync(ct))
            await _store.PurgeDeletedAsync(workspace.Id, ct);
    }

    // ---------- Traer ----------

    private async Task<bool> PullAsync(CachedWorkspace workspace, CancellationToken ct)
    {
        var since = workspace.LastSeq;
        var role = workspace.Role;
        var changed = false;
        try
        {
            while (true)
            {
                var page = await _api.GetChangesAsync(workspace.Id, since, PageSize, ct);
                if (page.MyRole != role)
                {
                    await ApplyRoleChangeAsync(workspace.Id, role, page.MyRole, ct);
                    role = page.MyRole;
                    changed = true;
                }

                changed |= await _store.ApplyChangesAsync(workspace.Id, page, ct);
                since = page.LastSeq;
                if (!page.HasMore) return changed;
            }
        }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return changed; // se perdió el acceso recién: lo resuelve la lista de workspaces del próximo ciclo
        }
    }

    /// <summary>
    /// Ganar o perder la vista de secretos no toca a las variables en el servidor, así que no llega por el feed: hay
    /// que pedirlas completas.
    /// </summary>
    private async Task ApplyRoleChangeAsync(Guid ws, WorkspaceRole from, WorkspaceRole to, CancellationToken ct)
    {
        var sawSecrets = from >= WorkspaceRole.Editor;
        var seesSecrets = to >= WorkspaceRole.Editor;
        if (sawSecrets == seesSecrets)
        {
            await _store.ReplaceVariablesAsync(ws, to, null, ct);
            return;
        }

        if (sawSecrets) await _store.WipeSecretsAsync(ws, ct);
        await _store.ReplaceVariablesAsync(ws, to, await _api.GetVariablesAsync(ws, ct), ct);
    }

    // ---------- Enviar ----------

    private async Task PushAsync(Func<OutboxEntry, bool> filter, CancellationToken ct)
    {
        while (await _store.TakeNextAsync(filter, ct) is { } item)
        {
            try
            {
                await _store.AckAsync(item, await SendAsync(item, ct));
            }
            catch (VersionConflictException e)
            {
                await ResolveVersionConflictAsync(item, e, ct);
            }
            catch (ApiException e) when (e is not SessionExpiredException && IsDefinitive(e))
            {
                await HandleRejectionAsync(item, e);
            }
            catch
            {
                await _store.ReleaseAsync();
                throw;
            }
            await PublishStatusAsync();
        }
    }

    private static bool IsDefinitive(ApiException e) =>
        e.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict;

    /// <summary>
    /// Si la respuesta a un envío anterior se perdió, el servidor ya tiene este contenido y el conflicto es con uno
    /// mismo: no hay nada que decidir.
    /// </summary>
    private async Task ResolveVersionConflictAsync(OutboxItem item, VersionConflictException e, CancellationToken ct)
    {
        object? remote;
        try
        {
            remote = await FetchRemoteAsync(item, ct);
        }
        catch
        {
            await _store.ReleaseAsync();
            throw;
        }

        if (remote is null) await _store.MarkConflictAsync(item, ConflictReason.Missing, "El elemento fue eliminado en el servidor.", null);
        else if (SyncItems.SameContent(remote, item.Dto!)) await _store.AckAsync(item, remote);
        else await _store.MarkConflictAsync(item, ConflictReason.Version, e.Message, remote);
    }

    private async Task HandleRejectionAsync(OutboxItem item, ApiException e)
    {
        if (e.Status == HttpStatusCode.Forbidden)
        {
            await _store.MarkConflictAsync(item, ConflictReason.Forbidden, "Ya no tenés permiso para editar este workspace.", null);
        }
        else if (e.Status == HttpStatusCode.NotFound)
        {
            await _store.MarkConflictAsync(item, ConflictReason.Missing, "El elemento fue eliminado en el servidor.", null);
        }
        else if (e.Status == HttpStatusCode.Conflict)
        {
            // Solo las variables tienen clave única; en lo demás, el alta chocó con un id ya borrado.
            await _store.MarkConflictAsync(
                item, item.Kind == ChangeKind.Variable ? ConflictReason.Duplicate : ConflictReason.Missing, e.Message, null);
        }
        else if (await _store.HasMissingParentAsync(item))
        {
            await _store.MarkConflictAsync(item, ConflictReason.Missing, "El elemento que lo contenía fue eliminado en el servidor.", null);
        }
        else
        {
            await _store.MarkConflictAsync(item, ConflictReason.Rejected, e.Message, null);
        }
    }

    private async Task<object?> SendAsync(OutboxItem item, CancellationToken ct)
    {
        var ws = item.WorkspaceId;
        var v = item.BaseVersion;
        return (item.Op, item.Dto) switch
        {
            (OutboxOp.Delete, _) => await DeleteAsync(item, ct),

            (OutboxOp.Create, WorkspaceDto d) => await _api.CreateWorkspaceAsync(d.Name, d.Id, ct),
            (OutboxOp.Create, EnvironmentDto d) => await _api.CreateEnvironmentAsync(ws, d.Name, d.Id, ct),
            (OutboxOp.Create, VariableDto d) => await _api.CreateVariableAsync(ws, new CreateVariableRequest(d.Key, d.Value ?? "", d.EnvironmentId, d.IsSecret, d.Id), ct),
            (OutboxOp.Create, AppDto d) => await _api.CreateAppAsync(ws, new CreateAppRequest(d.Name, d.Order, d.Id), ct),
            (OutboxOp.Create, FolderDto d) => await _api.CreateFolderAsync(ws, new CreateFolderRequest(d.AppId, d.Name, d.ParentFolderId, d.Order, d.Id), ct),
            (OutboxOp.Create, RequestDto d) => await _api.CreateRequestAsync(ws, new CreateRequestRequest(d.AppId, d.Name, d.Spec, d.FolderId, d.Order, d.Id), ct),
            (OutboxOp.Create, SampleDto d) => await _api.CreateSampleAsync(ws, new CreateSampleRequest(d.RequestId, d.Name, d.Overrides, d.Order, d.Id), ct),

            (OutboxOp.Update, EnvironmentDto d) => await _api.UpdateEnvironmentAsync(ws, d.Id, new UpdateEnvironmentRequest(d.Name, v), ct),
            (OutboxOp.Update, VariableDto d) => await _api.UpdateVariableAsync(ws, d.Id, new UpdateVariableRequest(d.Key, d.Value ?? "", d.IsSecret, v), ct),
            (OutboxOp.Update, AppDto d) => await _api.UpdateAppAsync(ws, d.Id, new UpdateAppRequest(d.Name, d.Order, v), ct),
            (OutboxOp.Update, FolderDto d) => await _api.UpdateFolderAsync(ws, d.Id, new UpdateFolderRequest(d.Name, d.ParentFolderId, d.Order, v), ct),
            (OutboxOp.Update, RequestDto d) => await _api.UpdateRequestAsync(ws, d.Id, new UpdateRequestRequest(d.Name, d.FolderId, d.Order, d.Spec, v), ct),
            (OutboxOp.Update, SampleDto d) => await _api.UpdateSampleAsync(ws, d.Id, new UpdateSampleRequest(d.Name, d.Order, d.Overrides, v), ct),

            _ => throw new InvalidOperationException($"Cambio sin envío definido: {item.Op} {item.Kind}."),
        };
    }

    private async Task<object?> DeleteAsync(OutboxItem item, CancellationToken ct)
    {
        var (ws, id) = (item.WorkspaceId, item.EntityId);
        try
        {
            await (item.Kind switch
            {
                ChangeKind.Environment => _api.DeleteEnvironmentAsync(ws, id, ct),
                ChangeKind.Variable => _api.DeleteVariableAsync(ws, id, ct),
                ChangeKind.App => _api.DeleteAppAsync(ws, id, ct),
                ChangeKind.Folder => _api.DeleteFolderAsync(ws, id, ct),
                ChangeKind.Request => _api.DeleteRequestAsync(ws, id, ct),
                ChangeKind.Sample => _api.DeleteSampleAsync(ws, id, ct),
                _ => throw new InvalidOperationException($"Borrado no soportado: {item.Kind}."),
            });
        }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            // Ya no estaba: era lo que se buscaba.
        }
        return null;
    }

    private async Task<object?> FetchRemoteAsync(OutboxItem item, CancellationToken ct)
    {
        var (ws, id) = (item.WorkspaceId, item.EntityId);
        return item.Kind switch
        {
            ChangeKind.Environment => (await _api.GetEnvironmentsAsync(ws, ct)).FirstOrDefault(x => x.Id == id),
            ChangeKind.Variable => (await _api.GetVariablesAsync(ws, ct)).FirstOrDefault(x => x.Id == id),
            ChangeKind.App => (await _api.GetAppsAsync(ws, ct)).FirstOrDefault(x => x.Id == id),
            ChangeKind.Folder => (await _api.GetFoldersAsync(ws, ct)).FirstOrDefault(x => x.Id == id),
            ChangeKind.Request => (await _api.GetRequestsAsync(ws, ct)).FirstOrDefault(x => x.Id == id),
            ChangeKind.Sample => (await _api.GetSamplesAsync(ws, ct)).FirstOrDefault(x => x.Id == id),
            _ => null,
        };
    }
}

/// <summary>Caché, motor y suscripción de un usuario en un servidor, durante una sesión de la app.</summary>
public sealed class SyncSession : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private SyncSession(SyncedWorkspaceStore store, SyncEngine engine)
    {
        Store = store;
        Engine = engine;
    }

    public SyncedWorkspaceStore Store { get; }
    public SyncEngine Engine { get; }

    /// <summary>Abre la caché de ese usuario en ese servidor: cada combinación tiene su propio archivo.</summary>
    public static async Task<SyncSession> OpenAsync(
        string directory, Uri server, Guid userId, RafApiClient api, ISecureStore secure, IChangeSubscriber? subscriber = null)
    {
        var serverHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(server.ToString())))[..16];
        var path = Path.Combine(directory, $"{serverHash}-{userId:N}.db");

        var store = await SyncedWorkspaceStore.OpenAsync(path, secure);
        var engine = new SyncEngine(
            store, new ApiWorkspaceStore(api), subscriber ?? new PollingChangeSubscriber(PollInterval, TimeProvider.System), path + ".lock");
        return new SyncSession(store, engine);
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.StopAsync();
        await Store.DisposeAsync();
    }
}
