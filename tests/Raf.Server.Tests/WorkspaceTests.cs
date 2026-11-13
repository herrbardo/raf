using System.Net;
using Raf.Contracts;
using Raf.Domain.Entities;
using Raf.Domain.Requests;

namespace Raf.Server.Tests;

public class WorkspaceTests(RafApp app)
{
    private async Task<(HttpClient Owner, WorkspaceDto Ws)> NewWorkspaceAsync()
    {
        var (_, owner) = await app.CreateActiveUserAsync();
        var ws = await owner.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Equipo"));
        return (owner, ws);
    }

    private async Task<HttpClient> AddMemberAsync(HttpClient owner, Guid ws, WorkspaceRole role)
    {
        var (user, client) = await app.CreateActiveUserAsync();
        await owner.PostOkAsync<MemberDto>($"/workspaces/{ws}/members", new AddMemberRequest(user.Id, role));
        return client;
    }

    [Fact]
    public async Task Creator_is_owner_and_non_members_get_404()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        Assert.Equal(WorkspaceRole.Owner, ws.MyRole);

        var (_, stranger) = await app.CreateActiveUserAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/workspaces/{ws.Id}/apps")).StatusCode);
        Assert.DoesNotContain(await stranger.GetOkAsync<List<WorkspaceDto>>("/workspaces"), w => w.Id == ws.Id);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_write_and_editor_can_write()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var viewer = await AddMemberAsync(owner, ws.Id, WorkspaceRole.Viewer);
        var editor = await AddMemberAsync(owner, ws.Id, WorkspaceRole.Editor);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/workspaces/{ws.Id}/apps")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostJsonAsync($"/workspaces/{ws.Id}/apps", new CreateAppRequest("No"))).StatusCode);

        await editor.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("Sí"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await editor.PutJsonAsync($"/workspaces/{ws.Id}", new UpdateWorkspaceRequest("Renombrado", ws.Version))).StatusCode);
    }

    [Fact]
    public async Task Ids_from_another_workspace_are_not_accessible()
    {
        var (ownerA, wsA) = await NewWorkspaceAsync();
        var (_, wsB) = await NewWorkspaceAsync();
        var appA = await ownerA.PostOkAsync<AppDto>($"/workspaces/{wsA.Id}/apps", new CreateAppRequest("A"));

        // ownerA no es miembro de B
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.GetAsync($"/workspaces/{wsB.Id}/apps/{appA.Id}")).StatusCode);
    }

    [Fact]
    public async Task Secret_variables_are_masked_for_viewers()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var viewer = await AddMemberAsync(owner, ws.Id, WorkspaceRole.Viewer);
        await owner.PostOkAsync<VariableDto>($"/workspaces/{ws.Id}/variables", new CreateVariableRequest("token", "s3cr3t", IsSecret: true));
        await owner.PostOkAsync<VariableDto>($"/workspaces/{ws.Id}/variables", new CreateVariableRequest("host", "api.test"));

        var asViewer = await viewer.GetOkAsync<List<VariableDto>>($"/workspaces/{ws.Id}/variables");
        Assert.Null(asViewer.Single(v => v.Key == "token").Value);
        Assert.Equal("api.test", asViewer.Single(v => v.Key == "host").Value);

        var asOwner = await owner.GetOkAsync<List<VariableDto>>($"/workspaces/{ws.Id}/variables");
        Assert.Equal("s3cr3t", asOwner.Single(v => v.Key == "token").Value);
    }

    [Fact]
    public async Task Same_variable_key_can_exist_globally_and_per_environment_but_not_twice()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var env = await owner.PostOkAsync<EnvironmentDto>($"/workspaces/{ws.Id}/environments", new CreateEnvironmentRequest("dev"));
        var url = $"/workspaces/{ws.Id}/variables";

        await owner.PostOkAsync<VariableDto>(url, new CreateVariableRequest("host", "global"));
        await owner.PostOkAsync<VariableDto>(url, new CreateVariableRequest("host", "dev", env.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostJsonAsync(url, new CreateVariableRequest("host", "otra"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostJsonAsync(url, new CreateVariableRequest("host", "otra", env.Id))).StatusCode);
    }

    [Fact]
    public async Task Stale_version_returns_409()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var appDto = await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("v1"));
        var url = $"/workspaces/{ws.Id}/apps/{appDto.Id}";

        await (await owner.PutJsonAsync(url, new UpdateAppRequest("v2", 0, appDto.Version))).EnsureStatusAsync(HttpStatusCode.OK);
        var stale = await owner.PutJsonAsync(url, new UpdateAppRequest("v3", 0, appDto.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task App_auth_is_saved_kept_when_omitted_and_cannot_inherit()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var b = $"/workspaces/{ws.Id}/apps";
        var bearer = new AuthSpec(AuthType.Bearer, Token: "{{token}}");

        var created = await owner.PostOkAsync<AppDto>(b, new CreateAppRequest("Api", Auth: bearer));
        Assert.Equal(bearer, created.Auth);
        var plain = await owner.PostOkAsync<AppDto>(b, new CreateAppRequest("Sin auth"));
        Assert.Equal(AuthType.None, plain.Auth?.Type);

        // Renombrar sin mandar la auth (como un cliente anterior) no la borra.
        var url = $"{b}/{created.Id}";
        await (await owner.PutJsonAsync(url, new UpdateAppRequest("Api 2", 0, created.Version))).EnsureStatusAsync(HttpStatusCode.OK);
        var renamed = await owner.GetOkAsync<AppDto>(url);
        Assert.Equal(bearer, renamed.Auth);

        var basic = new AuthSpec(AuthType.Basic, Username: "u", Password: "{{clave}}");
        await (await owner.PutJsonAsync(url, new UpdateAppRequest("Api 2", 0, renamed.Version, basic))).EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(basic, (await owner.GetOkAsync<AppDto>(url)).Auth);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostJsonAsync(b, new CreateAppRequest("Mal", Auth: AuthSpec.Inherit))).StatusCode);
        var current = await owner.GetOkAsync<AppDto>(url);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PutJsonAsync(url, new UpdateAppRequest("Api 2", 0, current.Version, AuthSpec.Inherit))).StatusCode);
    }

    [Fact]
    public async Task App_login_is_saved_kept_when_omitted_and_validated()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var b = $"/workspaces/{ws.Id}/apps";
        var login = AppLogin.Default with
        {
            Enabled = true,
            Request = AppLogin.Default.Request with
            {
                Url = "{{base}}/auth/token",
                Headers = [new KeyValueItem("X-Tenant", "{{tenant}}")],
                Body = new BodySpec(BodyType.Json, "{\"pass\":\"{{clave}}\"}"),
            },
            Expiry = TokenExpiry.ResponseField,
            ExpiryPath = "$.expires_in",
        };

        var created = await owner.PostOkAsync<AppDto>(b, new CreateAppRequest("Api", Login: login));
        Assert.Equivalent(login, created.Login, strict: true);
        Assert.Null((await owner.PostOkAsync<AppDto>(b, new CreateAppRequest("Sin login"))).Login);

        // Renombrar o cambiar solo la auth no borra el login.
        var url = $"{b}/{created.Id}";
        await (await owner.PutJsonAsync(url, new UpdateAppRequest("Api 2", 0, created.Version, AuthSpec.None))).EnsureStatusAsync(HttpStatusCode.OK);
        var renamed = await owner.GetOkAsync<AppDto>(url);
        Assert.Equivalent(login, renamed.Login, strict: true);

        // Apagarlo conserva la configuración.
        var off = login with { Enabled = false };
        await (await owner.PutJsonAsync(url, new UpdateAppRequest("Api 2", 0, renamed.Version, Login: off))).EnsureStatusAsync(HttpStatusCode.OK);
        var current = await owner.GetOkAsync<AppDto>(url);
        Assert.Equivalent(off, current.Login, strict: true);

        foreach (var bad in new[]
                 {
                     login with { Request = login.Request with { Auth = AuthSpec.Inherit } },
                     login with { Variable = "mi token" },
                     login with { Expiry = TokenExpiry.Fixed, ExpiryMinutes = null },
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutJsonAsync(url, new UpdateAppRequest("Api 2", 0, current.Version, Login: bad))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostJsonAsync(b, new CreateAppRequest("Mal", Login: bad))).StatusCode);
        }
    }

    [Fact]
    public async Task Request_keeps_inherited_auth_and_extractions()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var b = $"/workspaces/{ws.Id}";
        var appDto = await owner.PostOkAsync<AppDto>($"{b}/apps", new CreateAppRequest("Api"));
        var spec = RequestSpec.Empty with
        {
            Auth = AuthSpec.Inherit,
            Extractions = [new ResponseExtraction("token", ExtractionSource.Body, "$.access_token")],
        };

        var created = await owner.PostOkAsync<RequestDto>($"{b}/requests", new CreateRequestRequest(appDto.Id, "Login", spec));
        var read = await owner.GetOkAsync<RequestDto>($"{b}/requests/{created.Id}");

        Assert.Equal(AuthType.Inherit, read.Spec.Auth.Type);
        Assert.Equal(spec.Extractions, read.Spec.Extractions);
    }

    [Fact]
    public async Task Change_seq_is_unique_and_increasing_even_with_concurrent_writes()
    {
        var (owner, ws) = await NewWorkspaceAsync();

        var created = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest($"App {i}"))));

        var seqs = created.Select(a => a.ChangeSeq).Order().ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
        Assert.True(seqs[0] > ws.ChangeSeq);
    }

    [Fact]
    public async Task Deleting_a_folder_cascades_to_subfolders_requests_and_samples()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var b = $"/workspaces/{ws.Id}";
        var appDto = await owner.PostOkAsync<AppDto>($"{b}/apps", new CreateAppRequest("App"));
        var parent = await owner.PostOkAsync<FolderDto>($"{b}/folders", new CreateFolderRequest(appDto.Id, "Padre"));
        var child = await owner.PostOkAsync<FolderDto>($"{b}/folders", new CreateFolderRequest(appDto.Id, "Hija", parent.Id));
        var req = await owner.PostOkAsync<RequestDto>($"{b}/requests", new CreateRequestRequest(appDto.Id, "Req", FolderId: child.Id));
        await owner.PostOkAsync<SampleDto>($"{b}/samples", new CreateSampleRequest(req.Id, "Sample"));
        var rootReq = await owner.PostOkAsync<RequestDto>($"{b}/requests", new CreateRequestRequest(appDto.Id, "Raíz"));

        await (await owner.DeleteAsync($"{b}/folders/{parent.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);

        Assert.Empty(await owner.GetOkAsync<List<FolderDto>>($"{b}/folders"));
        Assert.Equal([rootReq.Id], (await owner.GetOkAsync<List<RequestDto>>($"{b}/requests")).Select(r => r.Id));
        Assert.Empty(await owner.GetOkAsync<List<SampleDto>>($"{b}/samples"));
    }

    [Fact]
    public async Task Folder_cannot_be_moved_into_its_own_descendant()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var b = $"/workspaces/{ws.Id}";
        var appDto = await owner.PostOkAsync<AppDto>($"{b}/apps", new CreateAppRequest("App"));
        var parent = await owner.PostOkAsync<FolderDto>($"{b}/folders", new CreateFolderRequest(appDto.Id, "Padre"));
        var child = await owner.PostOkAsync<FolderDto>($"{b}/folders", new CreateFolderRequest(appDto.Id, "Hija", parent.Id));

        var res = await owner.PutJsonAsync($"{b}/folders/{parent.Id}", new UpdateFolderRequest("Padre", child.Id, 0, parent.Version));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Sample_resolved_merges_parent_request()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var b = $"/workspaces/{ws.Id}";
        var appDto = await owner.PostOkAsync<AppDto>($"{b}/apps", new CreateAppRequest("App"));
        var spec = RequestSpec.Empty with { Url = "{{baseUrl}}/users", Headers = [new("Accept", "application/json")] };
        var req = await owner.PostOkAsync<RequestDto>($"{b}/requests", new CreateRequestRequest(appDto.Id, "Users", spec));
        var sample = await owner.PostOkAsync<SampleDto>($"{b}/samples",
            new CreateSampleRequest(req.Id, "POST", new SampleOverrides(Method: "POST", Headers: [new("X-Extra", "1")])));

        var resolved = await owner.GetOkAsync<RequestSpec>($"{b}/samples/{sample.Id}/resolved");

        Assert.Equal("POST", resolved.Method);
        Assert.Equal("{{baseUrl}}/users", resolved.Url);
        Assert.Equal(["Accept", "X-Extra"], resolved.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Ownership_transfer_demotes_previous_owner_to_editor()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var (member, memberClient) = await app.CreateActiveUserAsync();
        await owner.PostOkAsync<MemberDto>($"/workspaces/{ws.Id}/members", new AddMemberRequest(member.Id, WorkspaceRole.Editor));

        await (await owner.PostJsonAsync($"/workspaces/{ws.Id}/transfer", new TransferOwnershipRequest(member.Id)))
            .EnsureStatusAsync(HttpStatusCode.NoContent);

        Assert.Equal(WorkspaceRole.Owner, (await memberClient.GetOkAsync<WorkspaceDto>($"/workspaces/{ws.Id}")).MyRole);
        Assert.Equal(WorkspaceRole.Editor, (await owner.GetOkAsync<WorkspaceDto>($"/workspaces/{ws.Id}")).MyRole);
        var members = await owner.GetOkAsync<List<MemberDto>>($"/workspaces/{ws.Id}/members");
        Assert.Single(members, m => m.Role == WorkspaceRole.Owner);
    }

    [Fact]
    public async Task Members_can_leave_but_owner_cannot()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var (member, memberClient) = await app.CreateActiveUserAsync();
        await owner.PostOkAsync<MemberDto>($"/workspaces/{ws.Id}/members", new AddMemberRequest(member.Id, WorkspaceRole.Viewer));
        var ownerId = (await owner.GetOkAsync<UserDto>("/me")).Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync($"/workspaces/{ws.Id}/members/{ownerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.DeleteAsync($"/workspaces/{ws.Id}/members/{ownerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await memberClient.DeleteAsync($"/workspaces/{ws.Id}/members/{member.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await memberClient.GetAsync($"/workspaces/{ws.Id}")).StatusCode);
    }

    [Fact]
    public async Task Deleted_workspace_is_no_longer_accessible()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        await (await owner.DeleteAsync($"/workspaces/{ws.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/workspaces/{ws.Id}")).StatusCode);
    }
}
