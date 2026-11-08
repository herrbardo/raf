using System.Net;
using System.Net.Http.Json;
using Raf.Contracts;

namespace Raf.Server.Tests;

public class AuthTests(RafApp app)
{
    [Fact]
    public async Task Wrong_password_returns_401_and_lockout_after_5_attempts()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        var anon = app.Anonymous();
        for (var i = 0; i < 5; i++)
        {
            var res = await anon.PostJsonAsync("/auth/login", new LoginRequest(user.Email, "incorrecta"));
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }

        var locked = await anon.PostJsonAsync("/auth/login", new LoginRequest(user.Email, "NuevaClave123"));
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
    }

    [Fact]
    public async Task Concurrent_logins_of_the_same_user_succeed()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        var anon = app.Anonymous();
        // Un intento fallido deja AccessFailedCount > 0: cada login exitoso intentará resetearlo a la vez.
        await anon.PostJsonAsync("/auth/login", new LoginRequest(user.Email, "incorrecta"));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            anon.PostJsonAsync("/auth/login", new LoginRequest(user.Email, "NuevaClave123"))));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task Api_requires_authentication()
    {
        var res = await app.Anonymous().GetAsync("/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Temporary_password_must_be_changed_before_using_the_api()
    {
        var created = await app.CreateUserAsync();
        var login = await app.LoginRawAsync(created.User.Email, created.TemporaryPassword);
        Assert.True(login.User.MustChangePassword);

        var client = app.WithToken(login.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);

        var changed = await client.PostOkAsync<TokenResponse>(
            "/auth/change-password", new ChangePasswordRequest(created.TemporaryPassword, "OtraClave123"), HttpStatusCode.OK);
        Assert.False(changed.User.MustChangePassword);
        Assert.Equal(HttpStatusCode.OK, (await app.WithToken(changed.AccessToken).GetAsync("/workspaces")).StatusCode);

        // Cambiar la contraseña cierra las sesiones anteriores.
        var oldRefresh = await app.Anonymous().PostJsonAsync("/auth/refresh", new RefreshRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
    }

    [Fact]
    public async Task Change_password_rejects_wrong_current_password()
    {
        var (_, client) = await app.CreateActiveUserAsync();
        var res = await client.PostJsonAsync("/auth/change-password", new ChangePasswordRequest("mal", "OtraClave123"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_token_and_reuse_revokes_every_session()
    {
        var first = await app.LoginRawAsync(RafApp.AdminEmail, RafApp.AdminPassword);
        var anon = app.Anonymous();

        var second = await anon.PostOkAsync<TokenResponse>("/auth/refresh", new RefreshRequest(first.RefreshToken), HttpStatusCode.OK);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Reusar un token ya rotado se trata como robo: se revoca todo, incluido el nuevo.
        var reuse = await anon.PostJsonAsync("/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var afterReuse = await anon.PostJsonAsync("/auth/refresh", new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_token()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        var login = await app.LoginRawAsync(user.Email, "NuevaClave123");
        var anon = app.Anonymous();

        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostJsonAsync("/auth/logout", new LogoutRequest(login.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostJsonAsync("/auth/refresh", new RefreshRequest(login.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task Users_search_returns_enabled_users()
    {
        var (user, client) = await app.CreateActiveUserAsync("buscable-" + Guid.NewGuid().ToString("N")[..6]);
        var found = await client.GetOkAsync<List<UserSummaryDto>>($"/users?search={user.DisplayName}");
        Assert.Contains(found, u => u.Id == user.Id);
    }
}
