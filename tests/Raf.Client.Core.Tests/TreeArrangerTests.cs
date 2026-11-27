using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Tests;

public class TreeArrangerTests
{
    private static readonly Guid Ws = Guid.NewGuid();
    private readonly MemoryStore _store = new();
    private TreeArranger Arranger => new(_store, Ws);

    private Task<AppDto> App(string name, int order = 0) => _store.CreateAppAsync(Ws, new CreateAppRequest(name, order));

    private Task<FolderDto> Folder(Guid app, string name, Guid? parent = null, int order = 0) =>
        _store.CreateFolderAsync(Ws, new CreateFolderRequest(app, name, parent, order));

    private Task<RequestDto> Request(Guid app, string name, Guid? folder = null, int order = 0, RequestSpec? spec = null) =>
        _store.CreateRequestAsync(Ws, new CreateRequestRequest(app, name, spec, folder, order));

    private Task<SampleDto> Sample(Guid request, string name, int order = 0) =>
        _store.CreateSampleAsync(Ws, new CreateSampleRequest(request, name, null, order));

    private List<string> RequestsIn(Guid app, Guid? folder) =>
        _store.Requests.Where(r => r.AppId == app && r.FolderId == folder).OrderBy(r => r.Order).Select(r => $"{r.Name}:{r.Order}").ToList();

    [Fact]
    public async Task Moves_a_request_into_another_folder_of_the_same_app_at_the_end()
    {
        var app = await App("App");
        var folder = await Folder(app.Id, "Carpeta");
        await Request(app.Id, "Adentro", folder.Id, 0);
        var request = await Request(app.Id, "Suelto", null, 0);

        var result = await Arranger.MoveAsync(request, TreePlace.In(app.Id, folder.Id));

        Assert.Equal(TreeChange.Done, result.Change);
        Assert.Equal(["Adentro:0", "Suelto:1"], RequestsIn(app.Id, folder.Id));
        Assert.Empty(RequestsIn(app.Id, null));
    }

    [Fact]
    public async Task Reorders_before_an_anchor_and_only_updates_what_changes()
    {
        var app = await App("App");
        var a = await Request(app.Id, "A", null, 0);
        await Request(app.Id, "B", null, 1);
        var c = await Request(app.Id, "C", null, 2);
        await Request(app.Id, "D", null, 3);

        var result = await Arranger.MoveAsync(c, TreePlace.In(app.Id, null, a.Id, after: false));

        Assert.Equal(TreeChange.Done, result.Change);
        Assert.Equal(["C:0", "A:1", "B:2", "D:3"], RequestsIn(app.Id, null));
        Assert.Equal(3, _store.Updates); // D no se toca
    }

    [Fact]
    public async Task Dropping_in_the_same_place_changes_nothing()
    {
        var app = await App("App");
        var a = await Request(app.Id, "A", null, 0);
        var b = await Request(app.Id, "B", null, 1);

        var result = await Arranger.MoveAsync(b, TreePlace.In(app.Id, null, a.Id, after: true));

        Assert.Equal(TreeChange.Unchanged, result.Change);
        Assert.Equal(0, _store.Updates);
    }

    [Fact]
    public async Task Does_not_move_to_another_app_nor_a_folder_into_itself()
    {
        var app = await App("App");
        var other = await App("Otra", 1);
        var folder = await Folder(app.Id, "Padre");
        var sub = await Folder(app.Id, "Hija", folder.Id);
        var request = await Request(app.Id, "R");

        Assert.Equal(TreeChange.OtherApp, (await Arranger.MoveAsync(request, TreePlace.In(other.Id, null))).Change);
        Assert.Equal(TreeChange.OtherApp, (await Arranger.MoveAsync(folder, TreePlace.In(other.Id, null))).Change);
        Assert.Equal(TreeChange.IntoItself, (await Arranger.MoveAsync(folder, TreePlace.In(app.Id, folder.Id))).Change);
        Assert.Equal(TreeChange.IntoItself, (await Arranger.MoveAsync(folder, TreePlace.In(app.Id, sub.Id))).Change);
        Assert.Equal(0, _store.Updates);
    }

    [Fact]
    public async Task Moves_a_folder_with_its_content_and_reorders_apps()
    {
        var app = await App("A", 0);
        var second = await App("B", 1);
        var target = await Folder(app.Id, "Destino", null, 0);
        var folder = await Folder(app.Id, "Mudanza", null, 1);
        var inside = await Request(app.Id, "Adentro", folder.Id);

        Assert.Equal(TreeChange.Done, (await Arranger.MoveAsync(folder, TreePlace.In(app.Id, target.Id))).Change);
        Assert.Equal(target.Id, _store.Folders.Single(f => f.Id == folder.Id).ParentFolderId);
        Assert.Equal(folder.Id, _store.Requests.Single(r => r.Id == inside.Id).FolderId);

        Assert.Equal(TreeChange.Done, (await Arranger.MoveAsync(second, TreePlace.Apps(app.Id, after: false))).Change);
        Assert.Equal(["B", "A"], _store.Apps.OrderBy(a => a.Order).Select(a => a.Name));
    }

    [Fact]
    public async Task A_sample_only_moves_within_its_request()
    {
        var app = await App("App");
        var request = await Request(app.Id, "R");
        var other = await Request(app.Id, "Otro", null, 1);
        var first = await Sample(request.Id, "Uno", 0);
        var second = await Sample(request.Id, "Dos", 1);

        Assert.Equal(TreeChange.NotAllowed, (await Arranger.MoveAsync(second, TreePlace.InRequest(other.Id))).Change);
        Assert.Equal(TreeChange.Done, (await Arranger.MoveAsync(second, TreePlace.InRequest(request.Id, first.Id, after: false))).Change);
        Assert.Equal(["Dos", "Uno"], _store.Samples.OrderBy(s => s.Order).Select(s => s.Name));
    }

    [Fact]
    public async Task Copies_a_folder_to_another_app_keeping_the_name_when_free()
    {
        var app = await App("App");
        var other = await App("Otra", 1);
        var folder = await Folder(app.Id, "Usuarios");
        var request = await Request(app.Id, "Listar", folder.Id);
        await Sample(request.Id, "Caso");
        await Folder(other.Id, "Existente", null, 0);

        var result = await Arranger.CopyAsync(folder, TreePlace.In(other.Id, null));

        Assert.Equal(TreeChange.Done, result.Change);
        var copy = _store.Folders.Single(f => f.Id == result.Id);
        Assert.Equal((other.Id, (Guid?)null, "Usuarios", 1), (copy.AppId, copy.ParentFolderId, copy.Name, copy.Order));
        var copied = Assert.Single(_store.Requests, r => r.FolderId == copy.Id);
        Assert.Equal(other.Id, copied.AppId);
        Assert.Single(_store.Samples, s => s.RequestId == copied.Id);
        Assert.Equal(folder.Id, _store.Requests.Single(r => r.Id == request.Id).FolderId); // el original sigue
    }

    [Fact]
    public async Task Copying_next_to_an_existing_name_numbers_it_and_makes_room()
    {
        var app = await App("App");
        var a = await Request(app.Id, "A", null, 0);
        await Request(app.Id, "B", null, 1);

        var result = await Arranger.CopyAsync(a, TreePlace.In(app.Id, null, a.Id, after: true));

        Assert.Equal(TreeChange.Done, result.Change);
        Assert.Equal(["A:0", "A (1):1", "B:2"], RequestsIn(app.Id, null));
    }

    [Fact]
    public async Task Copies_a_sample_to_another_request_but_not_to_a_websocket()
    {
        var app = await App("App");
        var request = await Request(app.Id, "R");
        var target = await Request(app.Id, "Destino", null, 1);
        var socket = await Request(app.Id, "WS", null, 2, RequestSpec.EmptyWebSocket);
        var sample = await Sample(request.Id, "Caso");

        var result = await Arranger.CopyAsync(sample, TreePlace.InRequest(target.Id));
        Assert.Equal(TreeChange.Done, result.Change);
        Assert.Equal(target.Id, _store.Samples.Single(s => s.Id == result.Id).RequestId);

        Assert.Equal(TreeChange.NotAllowed, (await Arranger.CopyAsync(sample, TreePlace.InRequest(socket.Id))).Change);
    }

    [Fact]
    public async Task A_folder_copied_into_itself_copies_what_it_had()
    {
        var app = await App("App");
        var folder = await Folder(app.Id, "F");
        await Request(app.Id, "R", folder.Id);

        var result = await Arranger.CopyAsync(folder, TreePlace.In(app.Id, folder.Id));

        Assert.Equal(TreeChange.Done, result.Change);
        Assert.Single(_store.Requests, r => r.FolderId == result.Id);
        Assert.Equal(2, _store.Folders.Count);
    }
}
