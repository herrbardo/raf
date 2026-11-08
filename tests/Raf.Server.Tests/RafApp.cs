using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Raf.Contracts;
using Raf.Server.Tests;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(RafApp))]

namespace Raf.Server.Tests;

/// <summary>
/// Un servidor y un Postgres (Testcontainers) compartidos por todos los tests. Cada test crea sus propios
/// usuarios y workspaces, así que no interfieren entre sí.
/// </summary>
public sealed class RafApp : IAsyncLifetime
{
    public const string AdminEmail = "admin@test.local";
    public static readonly string AdminPassword = "Aa1" + Guid.NewGuid().ToString("N");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17").Build();
    private WebApplicationFactory<Program>? _factory;

    public async ValueTask InitializeAsync()
    {
        await _db.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Raf", _db.GetConnectionString());
            b.UseSetting("Jwt:SigningKey", Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
            b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
            b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
            b.UseSetting("Bootstrap:AdminMustChangePassword", "false");
        });
        _ = _factory.Server; // arranca el host: migra y crea el admin
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public HttpClient Anonymous() => _factory!.CreateClient();

    public async Task<TokenResponse> LoginRawAsync(string email, string password)
    {
        var res = await Anonymous().PostAsJsonAsync("/auth/login", new LoginRequest(email, password), Json);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    public HttpClient WithToken(string accessToken)
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public async Task<HttpClient> AdminAsync() => WithToken((await LoginRawAsync(AdminEmail, AdminPassword)).AccessToken);

    /// <summary>Crea un usuario con el admin y devuelve su contraseña temporal.</summary>
    public async Task<CreateUserResponse> CreateUserAsync(string? name = null)
    {
        name ??= "user-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await AdminAsync();
        var res = await admin.PostAsJsonAsync("/admin/users", new CreateUserRequest($"{name}@test.local", name), Json);
        await res.EnsureStatusAsync(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<CreateUserResponse>(Json))!;
    }

    /// <summary>Crea un usuario, hace login y cambia la contraseña temporal: queda listo para usar la API.</summary>
    public async Task<(UserDto User, HttpClient Client)> CreateActiveUserAsync(string? name = null)
    {
        var created = await CreateUserAsync(name);
        var login = await LoginRawAsync(created.User.Email, created.TemporaryPassword);
        var res = await WithToken(login.AccessToken).PostAsJsonAsync(
            "/auth/change-password", new ChangePasswordRequest(created.TemporaryPassword, "NuevaClave123"), Json);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        var tokens = (await res.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        return (tokens.User, WithToken(tokens.AccessToken));
    }
}

public static class HttpTestExtensions
{
    public static async Task EnsureStatusAsync(this HttpResponseMessage res, HttpStatusCode expected)
    {
        if (res.StatusCode != expected)
            Assert.Fail($"Esperaba {(int)expected} y llegó {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
    }

    public static async Task<T> PostOkAsync<T>(this HttpClient c, string url, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var res = await c.PostAsJsonAsync(url, body, RafApp.Json);
        await res.EnsureStatusAsync(expected);
        return (await res.Content.ReadFromJsonAsync<T>(RafApp.Json))!;
    }

    public static async Task<T> GetOkAsync<T>(this HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<T>(RafApp.Json))!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient c, string url, object body) =>
        c.PostAsJsonAsync(url, body, RafApp.Json);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient c, string url, object body) =>
        c.PutAsJsonAsync(url, body, RafApp.Json);
}
