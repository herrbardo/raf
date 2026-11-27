using Raf.Contracts;

namespace Raf.Client.Core.Workspaces;

/// <summary>
/// Dónde queda un elemento movido o pegado: un contenedor (la lista de Apps, una App o carpeta, o un request para
/// los samples) y, dentro de él, al lado de qué hermano del mismo tipo. Sin hermano, al final (o al principio si
/// <see cref="After"/> es false).
/// </summary>
public sealed record TreePlace(Guid? AppId, Guid? FolderId, Guid? RequestId, Guid? AnchorId, bool After)
{
    public static TreePlace Apps(Guid? anchor = null, bool after = true) => new(null, null, null, anchor, after);

    public static TreePlace In(Guid appId, Guid? folderId, Guid? anchor = null, bool after = true) =>
        new(appId, folderId, null, anchor, after);

    public static TreePlace InRequest(Guid requestId, Guid? anchor = null, bool after = true) =>
        new(null, null, requestId, anchor, after);
}

public enum TreeChange
{
    Done,

    /// <summary>Quedó donde estaba.</summary>
    Unchanged,

    /// <summary>Mover a otra App no se permite (Ticket-041): el servidor no cambia la App de un elemento.</summary>
    OtherApp,

    /// <summary>Una carpeta no puede ir dentro de sí misma ni de una de sus subcarpetas.</summary>
    IntoItself,

    /// <summary>El lugar no admite ese tipo de elemento, o el elemento ya no existe.</summary>
    NotAllowed,
}

public sealed record TreeResult(TreeChange Change, Guid? Id = null);

/// <summary>
/// Mueve y copia elementos del árbol (Ticket-041). Mover cambia la carpeta o el orden de lo que ya existe, siempre
/// dentro de su App; un sample solo se reordena dentro de su request. Copiar crea elementos nuevos con todo su
/// contenido (como duplicar) en cualquier lugar del workspace. En los dos casos los hermanos quedan numerados de
/// corrido y solo se actualiza lo que cambia de lugar.
/// </summary>
public sealed class TreeArranger(IWorkspaceStore store, Guid ws)
{
    private sealed record Tree(
        IReadOnlyList<AppDto> Apps, IReadOnlyList<FolderDto> Folders,
        IReadOnlyList<RequestDto> Requests, IReadOnlyList<SampleDto> Samples);

    private async Task<Tree> LoadAsync(CancellationToken ct) => new(
        await store.GetAppsAsync(ws, ct), await store.GetFoldersAsync(ws, ct),
        await store.GetRequestsAsync(ws, ct), await store.GetSamplesAsync(ws, ct));

    public async Task<TreeResult> MoveAsync(object source, TreePlace place, CancellationToken ct = default)
    {
        var tree = await LoadAsync(ct);
        switch (source)
        {
            case AppDto a when tree.Apps.FirstOrDefault(x => x.Id == a.Id) is { } app && IsApps(place):
                return await MoveAmongAsync(app, tree.Apps, place, x => x.Id, x => x.Order, changed: false,
                    (x, order) => store.UpdateAppAsync(ws, x.Id, new UpdateAppRequest(x.Name, order, x.Version, x.Auth, x.LoginId), ct));

            case FolderDto f when tree.Folders.FirstOrDefault(x => x.Id == f.Id) is { } folder && IsContainer(place, tree):
                if (place.AppId != folder.AppId) return new(TreeChange.OtherApp);
                if (IsSelfOrDescendant(tree, place.FolderId, folder.Id)) return new(TreeChange.IntoItself);
                return await MoveAmongAsync(folder, FoldersIn(tree, place), place, x => x.Id, x => x.Order,
                    changed: folder.ParentFolderId != place.FolderId,
                    (x, order) => store.UpdateFolderAsync(ws, x.Id, new UpdateFolderRequest(
                        x.Name, x.Id == folder.Id ? place.FolderId : x.ParentFolderId, order, x.Version), ct));

            case RequestDto r when tree.Requests.FirstOrDefault(x => x.Id == r.Id) is { } request && IsContainer(place, tree):
                if (place.AppId != request.AppId) return new(TreeChange.OtherApp);
                return await MoveAmongAsync(request, RequestsIn(tree, place), place, x => x.Id, x => x.Order,
                    changed: request.FolderId != place.FolderId,
                    (x, order) => store.UpdateRequestAsync(ws, x.Id, new UpdateRequestRequest(
                        x.Name, x.Id == request.Id ? place.FolderId : x.FolderId, order, x.Spec, x.Version), ct));

            case SampleDto s when tree.Samples.FirstOrDefault(x => x.Id == s.Id) is { } sample && place.RequestId == sample.RequestId:
                return await MoveAmongAsync(sample, SamplesIn(tree, place), place, x => x.Id, x => x.Order, changed: false,
                    (x, order) => store.UpdateSampleAsync(ws, x.Id, new UpdateSampleRequest(x.Name, order, x.Overrides, x.Version), ct));

            default:
                return new(TreeChange.NotAllowed);
        }
    }

    public async Task<TreeResult> CopyAsync(object source, TreePlace place, CancellationToken ct = default)
    {
        var tree = await LoadAsync(ct);
        var duplicator = new WorkspaceDuplicator(store, ws);
        switch (source)
        {
            case AppDto a when tree.Apps.FirstOrDefault(x => x.Id == a.Id) is { } app && IsApps(place):
            {
                var siblings = tree.Apps;
                var index = InsertIndex(siblings, place, x => x.Id, x => x.Order, exclude: null);
                var copy = await duplicator.DuplicateAppAsync(app, NameFor(app.Name, siblings.Select(x => x.Name)), index, ct);
                await RenumberAroundAsync(siblings, index, x => x.Order,
                    (x, order) => store.UpdateAppAsync(ws, x.Id, new UpdateAppRequest(x.Name, order, x.Version, x.Auth, x.LoginId), ct));
                return new(TreeChange.Done, copy.Id);
            }

            case FolderDto f when tree.Folders.FirstOrDefault(x => x.Id == f.Id) is { } folder && IsContainer(place, tree):
            {
                var siblings = FoldersIn(tree, place);
                var index = InsertIndex(siblings, place, x => x.Id, x => x.Order, exclude: null);
                var name = NameFor(folder.Name, NamesIn(tree, place));
                var copy = await duplicator.CopyFolderToAsync(folder, name, index, place.AppId!.Value, place.FolderId, ct);
                await RenumberAroundAsync(siblings, index, x => x.Order,
                    (x, order) => store.UpdateFolderAsync(ws, x.Id, new UpdateFolderRequest(x.Name, x.ParentFolderId, order, x.Version), ct));
                return new(TreeChange.Done, copy.Id);
            }

            case RequestDto r when tree.Requests.FirstOrDefault(x => x.Id == r.Id) is { } request && IsContainer(place, tree):
            {
                var siblings = RequestsIn(tree, place);
                var index = InsertIndex(siblings, place, x => x.Id, x => x.Order, exclude: null);
                var name = NameFor(request.Name, NamesIn(tree, place));
                var copy = await duplicator.CopyRequestToAsync(request, name, index, place.AppId!.Value, place.FolderId, ct);
                await RenumberAroundAsync(siblings, index, x => x.Order,
                    (x, order) => store.UpdateRequestAsync(ws, x.Id, new UpdateRequestRequest(x.Name, x.FolderId, order, x.Spec, x.Version), ct));
                return new(TreeChange.Done, copy.Id);
            }

            // Un WebSocket no tiene samples.
            case SampleDto s when tree.Samples.FirstOrDefault(x => x.Id == s.Id) is { } sample
                                  && tree.Requests.FirstOrDefault(x => x.Id == place.RequestId) is { Spec.IsWebSocket: false } target:
            {
                var siblings = SamplesIn(tree, place);
                var index = InsertIndex(siblings, place, x => x.Id, x => x.Order, exclude: null);
                var copy = await duplicator.CopySampleToAsync(sample, NameFor(sample.Name, siblings.Select(x => x.Name)), index, target.Id, ct);
                await RenumberAroundAsync(siblings, index, x => x.Order,
                    (x, order) => store.UpdateSampleAsync(ws, x.Id, new UpdateSampleRequest(x.Name, order, x.Overrides, x.Version), ct));
                return new(TreeChange.Done, copy.Id);
            }

            default:
                return new(TreeChange.NotAllowed);
        }
    }

    private static bool IsApps(TreePlace place) => place is { AppId: null, FolderId: null, RequestId: null };

    /// <summary>Una App, o una carpeta que existe y es de esa App.</summary>
    private static bool IsContainer(TreePlace place, Tree tree) =>
        place is { AppId: { } app, RequestId: null }
        && tree.Apps.Any(a => a.Id == app)
        && (place.FolderId is not { } folder || tree.Folders.Any(f => f.Id == folder && f.AppId == app));

    private static bool IsSelfOrDescendant(Tree tree, Guid? candidate, Guid folderId)
    {
        for (var cursor = candidate; cursor is { } id; cursor = tree.Folders.FirstOrDefault(f => f.Id == id)?.ParentFolderId)
            if (id == folderId) return true;
        return false;
    }

    private static List<FolderDto> FoldersIn(Tree tree, TreePlace place) =>
        tree.Folders.Where(f => f.AppId == place.AppId && f.ParentFolderId == place.FolderId).OrderBy(f => f.Order).ToList();

    private static List<RequestDto> RequestsIn(Tree tree, TreePlace place) =>
        tree.Requests.Where(r => r.AppId == place.AppId && r.FolderId == place.FolderId).OrderBy(r => r.Order).ToList();

    private static List<SampleDto> SamplesIn(Tree tree, TreePlace place) =>
        tree.Samples.Where(s => s.RequestId == place.RequestId).OrderBy(s => s.Order).ToList();

    /// <summary>Los nombres que se ven en el contenedor: carpetas y requests comparten la lista.</summary>
    private static IEnumerable<string> NamesIn(Tree tree, TreePlace place) =>
        FoldersIn(tree, place).Select(f => f.Name).Concat(RequestsIn(tree, place).Select(r => r.Name));

    /// <summary>La copia conserva el nombre si nadie lo usa en el destino; si no, "nombre (n)", como al duplicar.</summary>
    private static string NameFor(string name, IEnumerable<string> siblings)
    {
        var names = siblings.ToList();
        return names.Contains(name, StringComparer.OrdinalIgnoreCase) ? DuplicateName.Next(name, names) : name;
    }

    private static int InsertIndex<T>(IReadOnlyList<T> siblings, TreePlace place, Func<T, Guid> id, Func<T, int> order, Guid? exclude)
    {
        var others = siblings.Where(x => id(x) != exclude).OrderBy(order).ToList();
        var anchor = others.FindIndex(x => id(x) == place.AnchorId);
        if (anchor < 0) return place.After ? others.Count : 0;
        return place.After ? anchor + 1 : anchor;
    }

    private static async Task<TreeResult> MoveAmongAsync<T>(
        T item, IReadOnlyList<T> siblings, TreePlace place, Func<T, Guid> id, Func<T, int> order, bool changed,
        Func<T, int, Task> update)
    {
        var itemId = id(item);
        var others = siblings.Where(x => id(x) != itemId).OrderBy(order).ToList();
        others.Insert(InsertIndex(siblings, place, id, order, exclude: itemId), item);

        var before = siblings.OrderBy(order).Select(id).ToList();
        if (!changed && before.SequenceEqual(others.Select(id))) return new(TreeChange.Unchanged, itemId);

        for (var i = 0; i < others.Count; i++)
            if (order(others[i]) != i || (changed && id(others[i]) == itemId))
                await update(others[i], i);
        return new(TreeChange.Done, itemId);
    }

    /// <summary>Tras crear la copia en <paramref name="index"/>, los hermanos de antes y después quedan correlativos.</summary>
    private static async Task RenumberAroundAsync<T>(IReadOnlyList<T> siblings, int index, Func<T, int> order, Func<T, int, Task> update)
    {
        var ordered = siblings.OrderBy(order).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var target = i < index ? i : i + 1;
            if (order(ordered[i]) != target) await update(ordered[i], target);
        }
    }
}
