using System.Net;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Server.Tests;

public class ChangesTests(RafApp app)
{
    private async Task<(HttpClient Owner, WorkspaceDto Ws)> NewWorkspaceAsync()
    {
        var (_, owner) = await app.CreateActiveUserAsync();
        return (owner, await owner.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Equipo")));
    }

    private static Task<ChangesResponse> ChangesAsync(HttpClient c, Guid ws, long since = 0, int limit = 500) =>
        c.GetOkAsync<ChangesResponse>($"/workspaces/{ws}/changes?since={since}&limit={limit}");

    [Fact]
    public async Task Entrega_altas_y_solo_lo_posterior_a_since()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("Uno"));

        var first = await ChangesAsync(owner, ws.Id);
        Assert.Equal("Equipo", first.Workspace!.Name);
        Assert.Single(first.Members);
        Assert.Equal("Uno", Assert.Single(first.Apps).Name);
        Assert.False(first.HasMore);

        var nothing = await ChangesAsync(owner, ws.Id, first.LastSeq);
        Assert.Empty(nothing.Apps);
        Assert.Equal(first.LastSeq, nothing.LastSeq);

        await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("Dos"));
        Assert.Equal("Dos", Assert.Single((await ChangesAsync(owner, ws.Id, first.LastSeq)).Apps).Name);
    }

    [Fact]
    public async Task Pagina_sin_perder_ni_repetir_elementos()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        for (var i = 0; i < 7; i++)
            await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest($"App{i}"));

        var seen = new List<string>();
        long since = 0;
        var pages = 0;
        while (true)
        {
            var page = await ChangesAsync(owner, ws.Id, since, 3);
            seen.AddRange(page.Apps.Select(a => a.Name));
            since = page.LastSeq;
            pages++;
            if (!page.HasMore) break;
        }

        Assert.True(pages >= 3);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"App{i}"), seen);
    }

    [Fact]
    public async Task Un_mismo_cambio_no_se_parte_entre_paginas()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var a = await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("A"));
        for (var i = 0; i < 4; i++)
            await owner.PostOkAsync<RequestDto>($"/workspaces/{ws.Id}/requests", new CreateRequestRequest(a.Id, $"R{i}"));
        var baseline = (await ChangesAsync(owner, ws.Id)).LastSeq;

        await owner.DeleteAsync($"/workspaces/{ws.Id}/apps/{a.Id}"); // borra 5 filas con el mismo número de cambio

        var page = await ChangesAsync(owner, ws.Id, baseline, 2);
        Assert.Equal(5, page.Deleted.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task HasMore_detecta_cambios_posteriores_a_un_lote_grande()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var a = await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("A"));
        for (var i = 0; i < 4; i++)
            await owner.PostOkAsync<RequestDto>($"/workspaces/{ws.Id}/requests", new CreateRequestRequest(a.Id, $"R{i}"));
        var baseline = (await ChangesAsync(owner, ws.Id)).LastSeq;
        await owner.DeleteAsync($"/workspaces/{ws.Id}/apps/{a.Id}");
        await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("Despues"));

        var page = await ChangesAsync(owner, ws.Id, baseline, 2);
        Assert.True(page.HasMore);
        Assert.Equal("Despues", Assert.Single((await ChangesAsync(owner, ws.Id, page.LastSeq)).Apps).Name);
    }

    [Fact]
    public async Task Viewer_recibe_secretos_sin_valor()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var (viewerUser, viewer) = await app.CreateActiveUserAsync();
        await owner.PostOkAsync<MemberDto>($"/workspaces/{ws.Id}/members", new AddMemberRequest(viewerUser.Id, WorkspaceRole.Viewer));
        await owner.PostOkAsync<VariableDto>($"/workspaces/{ws.Id}/variables", new CreateVariableRequest("token", "s3cr3t", IsSecret: true));

        var asViewer = await ChangesAsync(viewer, ws.Id);
        Assert.Equal(WorkspaceRole.Viewer, asViewer.MyRole);
        Assert.Null(Assert.Single(asViewer.Variables).Value);
        Assert.Equal("s3cr3t", Assert.Single((await ChangesAsync(owner, ws.Id)).Variables).Value);
    }

    [Fact]
    public async Task Cambio_de_rol_y_baja_de_miembro_entran_en_el_feed()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var (user, _) = await app.CreateActiveUserAsync();
        await owner.PostOkAsync<MemberDto>($"/workspaces/{ws.Id}/members", new AddMemberRequest(user.Id, WorkspaceRole.Viewer));
        var baseline = (await ChangesAsync(owner, ws.Id)).LastSeq;

        await owner.PutJsonAsync($"/workspaces/{ws.Id}/members/{user.Id}", new UpdateMemberRequest(WorkspaceRole.Editor));
        var changed = await ChangesAsync(owner, ws.Id, baseline);
        var member = Assert.Single(changed.Members);
        Assert.Equal(WorkspaceRole.Editor, member.Role);
        Assert.Equal(2, member.Version);

        await owner.DeleteAsync($"/workspaces/{ws.Id}/members/{user.Id}");
        var removed = await ChangesAsync(owner, ws.Id, changed.LastSeq);
        var tombstone = Assert.Single(removed.Deleted);
        Assert.Equal((ChangeKind.Member, user.Id), (tombstone.Kind, tombstone.Id));
    }

    [Fact]
    public async Task Reintentar_un_alta_devuelve_el_elemento_ya_creado()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var id = Guid.CreateVersion7();
        var req = new CreateAppRequest("Una", Id: id);

        var first = await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", req);
        var retry = await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", req);

        Assert.Equal(first, retry);
        Assert.Single(await owner.GetOkAsync<List<AppDto>>($"/workspaces/{ws.Id}/apps"));
    }

    [Fact]
    public async Task Reintentar_el_alta_de_un_workspace_es_idempotente()
    {
        var (_, owner) = await app.CreateActiveUserAsync();
        var req = new CreateWorkspaceRequest("Equipo", Guid.CreateVersion7());

        var first = await owner.PostOkAsync<WorkspaceDto>("/workspaces", req);
        Assert.Equal(first, await owner.PostOkAsync<WorkspaceDto>("/workspaces", req));
    }

    [Fact]
    public async Task Un_alta_reintentada_no_resucita_lo_borrado()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var req = new CreateAppRequest("Una", Id: Guid.CreateVersion7());
        await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", req);
        await owner.DeleteAsync($"/workspaces/{ws.Id}/apps/{req.Id}");

        var retry = await owner.PostJsonAsync($"/workspaces/{ws.Id}/apps", req);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
    }

    [Fact]
    public async Task Id_de_otro_workspace_no_se_devuelve_como_reintento()
    {
        var (ownerA, wsA) = await NewWorkspaceAsync();
        var (ownerB, wsB) = await NewWorkspaceAsync();
        var req = new CreateAppRequest("Privada", Id: Guid.CreateVersion7());
        await ownerA.PostOkAsync<AppDto>($"/workspaces/{wsA.Id}/apps", req);

        var attempt = await ownerB.PostJsonAsync($"/workspaces/{wsB.Id}/apps", req);
        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
    }

    [Fact]
    public async Task Avisa_cada_cambio_confirmado_con_su_numero()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var a = await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("A"));
        Assert.Contains(a.ChangeSeq, app.Notifier.For(ws.Id));
    }
}
