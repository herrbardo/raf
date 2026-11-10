using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Raf.Server.Auth;
using Raf.Server.Data;

namespace Raf.Server.Realtime;

/// <summary>Lo que el servidor le avisa al cliente. Nunca viaja contenido: el cliente hace pull.</summary>
public interface IChangesClient
{
    /// <summary>El workspace tiene cambios hasta <paramref name="changeSeq"/>.</summary>
    Task Changed(Guid workspaceId, long changeSeq);

    /// <summary>Cambió algo del propio usuario (por ejemplo, lo agregaron a un workspace): conviene sincronizar.</summary>
    Task Refresh();
}

/// <summary>
/// Avisos de cambios por WebSocket. El JWT se valida al conectar (con el corte por usuario deshabilitado) y la
/// membresía se valida en <see cref="Watch"/>, antes de sumar la conexión al grupo de cada workspace.
/// </summary>
[Authorize]
public sealed class ChangesHub(RafDbContext db, ConnectionRegistry registry) : Hub<IChangesClient>
{
    public const string Path = "/hubs/changes";
    private const string WatchedKey = "raf.watched";

    public static string WorkspaceGroup(Guid id) => $"ws:{id}";

    public static string UserGroup(Guid id) => $"user:{id}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.GetUserId();
        registry.Add(userId, Context);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Fija los workspaces de los que la conexión recibe avisos: reemplaza la lista anterior. Los que no son del
    /// usuario se ignoran. Devuelve los aceptados.
    /// </summary>
    public async Task<Guid[]> Watch(Guid[] workspaceIds)
    {
        var userId = Context.User!.GetUserId();
        var requested = workspaceIds.Distinct().Take(500).ToList();
        var allowed = await (
            from m in db.WorkspaceMembers
            join w in db.Workspaces on m.WorkspaceId equals w.Id
            where m.UserId == userId && requested.Contains(m.WorkspaceId) && !m.IsDeleted && !w.IsDeleted
            select m.WorkspaceId).ToListAsync(Context.ConnectionAborted);

        var previous = Context.Items.TryGetValue(WatchedKey, out var value) ? (HashSet<Guid>)value! : [];
        var next = allowed.ToHashSet();
        foreach (var id in previous.Except(next)) await Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkspaceGroup(id));
        foreach (var id in next.Except(previous)) await Groups.AddToGroupAsync(Context.ConnectionId, WorkspaceGroup(id));
        Context.Items[WatchedKey] = next;
        return [.. next];
    }
}

/// <summary>Conexiones abiertas en esta tarea, para cortar las de un usuario deshabilitado.</summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (Guid UserId, HubCallerContext Context)> _connections = new();

    public void Add(Guid userId, HubCallerContext context) => _connections[context.ConnectionId] = (userId, context);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public int AbortUser(Guid userId)
    {
        var aborted = 0;
        foreach (var (_, entry) in _connections.Where(c => c.Value.UserId == userId))
        {
            entry.Context.Abort();
            aborted++;
        }
        return aborted;
    }
}
