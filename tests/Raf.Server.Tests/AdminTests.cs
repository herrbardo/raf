using System.Net;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Server.Tests;

public class AdminTests(RafApp app)
{
    [Fact]
    public async Task Non_admin_cannot_use_admin_endpoints()
    {
        var (_, client) = await app.CreateActiveUserAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostJsonAsync("/admin/users", new CreateUserRequest("x@test.local", "X"))).StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        var created = await app.CreateUserAsync();
        var admin = await app.AdminAsync();
        var res = await admin.PostJsonAsync("/admin/users", new CreateUserRequest(created.User.Email, "Otro"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Disabled_user_cannot_login_or_refresh()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        var login = await app.LoginRawAsync(user.Email, "NuevaClave123");
        var admin = await app.AdminAsync();

        await (await admin.PostAsync($"/admin/users/{user.Id}/disable", null)).EnsureStatusAsync(HttpStatusCode.OK);

        var anon = app.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostJsonAsync("/auth/login", new LoginRequest(user.Email, "NuevaClave123"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostJsonAsync("/auth/refresh", new RefreshRequest(login.RefreshToken))).StatusCode);

        await (await admin.PostAsync($"/admin/users/{user.Id}/enable", null)).EnsureStatusAsync(HttpStatusCode.OK);
        await app.LoginRawAsync(user.Email, "NuevaClave123");
    }

    [Fact]
    public async Task Reset_password_issues_a_new_temporary_password()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        var admin = await app.AdminAsync();

        var reset = await admin.PostOkAsync<ResetPasswordResponse>($"/admin/users/{user.Id}/reset-password", new { }, HttpStatusCode.OK);

        var anon = app.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostJsonAsync("/auth/login", new LoginRequest(user.Email, "NuevaClave123"))).StatusCode);
        var login = await app.LoginRawAsync(user.Email, reset.TemporaryPassword);
        Assert.True(login.User.MustChangePassword);
    }

    [Fact]
    public async Task Admin_cannot_disable_self_or_drop_the_last_admin_role()
    {
        var admin = await app.AdminAsync();
        var me = await admin.GetOkAsync<UserDto>("/me");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/admin/users/{me.Id}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PutJsonAsync($"/admin/users/{me.Id}", new UpdateUserRequest(me.Email, me.DisplayName, IsAdmin: false))).StatusCode);
    }

    [Fact]
    public async Task Admin_can_reassign_workspace_owner()
    {
        var (_, owner) = await app.CreateActiveUserAsync();
        var (newOwner, newOwnerClient) = await app.CreateActiveUserAsync();
        var ws = await owner.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Huérfano"));
        var admin = await app.AdminAsync();

        await (await admin.PutJsonAsync($"/admin/workspaces/{ws.Id}/owner", new SetOwnerRequest(newOwner.Id)))
            .EnsureStatusAsync(HttpStatusCode.NoContent);

        var mine = await newOwnerClient.GetOkAsync<WorkspaceDto>($"/workspaces/{ws.Id}");
        Assert.Equal(WorkspaceRole.Owner, mine.MyRole);
        var previous = await owner.GetOkAsync<WorkspaceDto>($"/workspaces/{ws.Id}");
        Assert.Equal(WorkspaceRole.Editor, previous.MyRole);

        var listed = await admin.GetOkAsync<List<AdminWorkspaceDto>>("/admin/workspaces");
        Assert.Equal(newOwner.Id, listed.Single(w => w.Id == ws.Id).Owner!.Id);
    }

    [Fact]
    public async Task Disabled_user_is_cut_off_even_with_a_valid_access_token()
    {
        var (user, client) = await app.CreateActiveUserAsync();
        var ws = await client.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Antes del corte"));
        var admin = await app.AdminAsync();

        await (await admin.PostAsync($"/admin/users/{user.Id}/disable", null)).EnsureStatusAsync(HttpStatusCode.OK);

        // El mismo access token, todavía vigente, ya no sirve para nada.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/workspaces/{ws.Id}/changes")).StatusCode);

        await (await admin.PostAsync($"/admin/users/{user.Id}/enable", null)).EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Removing_the_admin_role_takes_effect_before_the_token_expires()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        var admin = await app.AdminAsync();
        await (await admin.PutJsonAsync($"/admin/users/{user.Id}", new UpdateUserRequest(user.Email, user.DisplayName, IsAdmin: true)))
            .EnsureStatusAsync(HttpStatusCode.OK);

        var promoted = app.WithToken((await app.LoginRawAsync(user.Email, "NuevaClave123")).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await promoted.GetAsync("/admin/users")).StatusCode);

        await (await admin.PutJsonAsync($"/admin/users/{user.Id}", new UpdateUserRequest(user.Email, user.DisplayName, IsAdmin: false)))
            .EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await promoted.GetAsync("/admin/users")).StatusCode);
    }
}
