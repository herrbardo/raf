using System.Net;
using Raf.Client.Core.Api;
using Raf.Client.Core.Local;
using Raf.Client.Core.Session;
using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Server.Tests;

/// <summary>Las pantallas de gestión del cliente, a través de <see cref="ManagementApi"/>, contra el servidor real.</summary>
public class ManagementApiTests(RafApp app)
{
    private async Task<ManagementApi> ApiForAsync(string email, string password)
    {
        var http = app.Anonymous();
        var lockPath = Path.Combine(Path.GetTempPath(), "raf-mgmt-" + Guid.NewGuid().ToString("N"), "session.lock");
        var session = new SessionManager(http, new MemorySecureStore(), TimeProvider.System, lockPath);
        session.SetServer(new Uri("http://localhost/"));
        await session.LoginAsync(email, password);
        return new ManagementApi(new RafApiClient(http, session));
    }

    private async Task<(UserDto User, ManagementApi Api)> NewUserAsync()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        return (user, await ApiForAsync(user.Email, "NuevaClave123"));
    }

    [Fact]
    public async Task Owner_manages_members_roles_and_ownership()
    {
        var (_, ownerClient) = await app.CreateActiveUserAsync();
        var ws = await ownerClient.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Gestión"));
        var me = await ownerClient.GetOkAsync<UserDto>("/me");
        var owner = await ApiForAsync(me.Email, "NuevaClave123");
        var (editor, editorApi) = await NewUserAsync();
        var (viewer, viewerApi) = await NewUserAsync();

        var found = await owner.SearchUsersAsync(editor.Email);
        Assert.Contains(found, u => u.Id == editor.Id);

        await owner.AddMemberAsync(ws.Id, editor.Id, WorkspaceRole.Editor);
        await owner.AddMemberAsync(ws.Id, viewer.Id, WorkspaceRole.Viewer);
        await owner.ChangeRoleAsync(ws.Id, viewer.Id, WorkspaceRole.Editor);
        var members = await owner.GetMembersAsync(ws.Id);
        Assert.Equal(WorkspaceRole.Editor, members.Single(m => m.UserId == viewer.Id).Role);

        // Un Editor no gestiona miembros, pero puede salir.
        var denied = await Assert.ThrowsAsync<ApiException>(() => editorApi.RemoveMemberAsync(ws.Id, viewer.Id));
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        await viewerApi.RemoveMemberAsync(ws.Id, viewer.Id);
        Assert.DoesNotContain(await owner.GetMembersAsync(ws.Id), m => m.UserId == viewer.Id);

        var renamed = await owner.RenameWorkspaceAsync(ws.Id, "Gestión 2", ws.Version);
        Assert.Equal("Gestión 2", renamed.Name);

        await owner.TransferOwnershipAsync(ws.Id, editor.Id);
        Assert.Equal(WorkspaceRole.Owner, (await editorApi.GetWorkspaceAsync(ws.Id)).MyRole);
        Assert.Equal(WorkspaceRole.Editor, (await owner.GetWorkspaceAsync(ws.Id)).MyRole);

        await editorApi.DeleteWorkspaceAsync(ws.Id);
        var gone = await Assert.ThrowsAsync<ApiException>(() => owner.GetWorkspaceAsync(ws.Id));
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
    }

    [Fact]
    public async Task Admin_manages_users_and_reassigns_owners()
    {
        var admin = await ApiForAsync(RafApp.AdminEmail, RafApp.AdminPassword);
        var name = "gestion-" + Guid.NewGuid().ToString("N")[..8];

        var created = await admin.CreateUserAsync(new CreateUserRequest($"{name}@test.local", name));
        Assert.True(created.User.MustChangePassword);
        Assert.False(string.IsNullOrWhiteSpace(created.TemporaryPassword));

        var updated = await admin.UpdateUserAsync(created.User.Id, new UpdateUserRequest(created.User.Email, "Renombrado", IsAdmin: false));
        Assert.Equal("Renombrado", updated.DisplayName);

        var temporary = await admin.ResetPasswordAsync(created.User.Id);
        Assert.NotEqual(created.TemporaryPassword, temporary);
        Assert.True((await app.LoginRawAsync(created.User.Email, temporary)).User.MustChangePassword);

        Assert.True((await admin.DisableUserAsync(created.User.Id)).IsDisabled);
        Assert.False((await admin.EnableUserAsync(created.User.Id)).IsDisabled);
        Assert.Contains(await admin.GetUsersAsync(), u => u.Id == created.User.Id);

        var (_, ownerClient) = await app.CreateActiveUserAsync();
        var ws = await ownerClient.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Reasignable"));
        await admin.SetOwnerAsync(ws.Id, created.User.Id);
        var listed = (await admin.GetAllWorkspacesAsync()).Single(w => w.Id == ws.Id);
        Assert.Equal(created.User.Id, listed.Owner!.Id);
    }
}
