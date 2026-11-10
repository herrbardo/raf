using System.Net;
using System.Net.Http.Json;
using Raf.Client.Core.Api;
using Raf.Client.Core.Local;
using Raf.Client.Core.Session;
using Raf.Contracts;

namespace Raf.Server.Tests;

/// <summary>Versión mínima de cliente: el servidor responde 426 y el cliente lo detecta aunque la llamada sea de fondo.</summary>
public class ClientCompatibilityTests(RafApp app)
{
    private HttpClient WithVersion(string version)
    {
        var client = app.Anonymous();
        client.DefaultRequestHeaders.Add(RafHeaders.ClientVersion, version);
        return client;
    }

    [Fact]
    public async Task Version_endpoint_is_public_and_reports_the_minimum()
    {
        var info = await app.Anonymous().GetFromJsonAsync<ServerInfoResponse>("/version", RafApp.Json);
        Assert.Equal(RafApp.MinClientVersion, info!.MinClientVersion);
        Assert.True(AppVersion.TryParse(info.Version, out _));
    }

    [Theory]
    [InlineData("2.0.9")]
    [InlineData("1.9.0+abc123")]
    public async Task Older_clients_get_426_even_before_authenticating(string version)
    {
        var res = await WithVersion(version).PostAsJsonAsync("/auth/login",
            new LoginRequest(RafApp.AdminEmail, "contraseña-incorrecta"), RafApp.Json);

        Assert.Equal(HttpStatusCode.UpgradeRequired, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>(RafApp.Json);
        Assert.Equal(AppVersion.OutdatedTitle, problem!["title"].ToString());
        Assert.Equal(RafApp.MinClientVersion, problem["minClientVersion"].ToString());
    }

    [Theory]
    [InlineData("2.1.0")]
    [InlineData("3.0.0")]
    [InlineData("2.1.0-beta.1")] // la etiqueta de prerelease no se compara: cuenta como la 2.1.0
    [InlineData("no-es-una-versión")] // no se puede comparar: se deja pasar
    public async Task Current_clients_and_unknown_versions_pass(string version)
    {
        var res = await WithVersion(version).PostAsJsonAsync("/auth/login",
            new LoginRequest(RafApp.AdminEmail, RafApp.AdminPassword), RafApp.Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Health_is_never_blocked()
    {
        var res = await WithVersion("0.0.1").GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Client_handler_reports_outdated_and_api_throws_ClientOutdatedException()
    {
        var handler = new ClientVersionHandler("1.0.0");
        string? reported = null;
        handler.Outdated += min => reported = min;
        var http = app.ClientWith(handler);
        var lockPath = Path.Combine(Path.GetTempPath(), "raf-compat-" + Guid.NewGuid().ToString("N"), "session.lock");
        var session = new SessionManager(http, new MemorySecureStore(), TimeProvider.System, lockPath);
        session.SetServer(new Uri("http://localhost/"));

        var e = await Assert.ThrowsAsync<ClientOutdatedException>(() => session.LoginAsync(RafApp.AdminEmail, RafApp.AdminPassword));

        Assert.Equal(RafApp.MinClientVersion, reported);
        Assert.Equal(RafApp.MinClientVersion, e.MinVersion);
        Assert.Contains("1.0.0", e.Message);
    }

    [Fact]
    public async Task Client_handler_sends_its_version_and_current_clients_work()
    {
        var handler = new ClientVersionHandler(RafApp.MinClientVersion);
        var fired = false;
        handler.Outdated += _ => fired = true;
        var http = app.ClientWith(handler);
        var lockPath = Path.Combine(Path.GetTempPath(), "raf-compat-" + Guid.NewGuid().ToString("N"), "session.lock");
        var session = new SessionManager(http, new MemorySecureStore(), TimeProvider.System, lockPath);
        session.SetServer(new Uri("http://localhost/"));

        await session.LoginAsync(RafApp.AdminEmail, RafApp.AdminPassword);
        var me = await new RafApiClient(http, session).GetAsync<UserDto>("me");

        Assert.Equal(RafApp.AdminEmail, me.Email);
        Assert.False(fired);
    }
}
