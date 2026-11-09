using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Raf.Contracts;
using Raf.Server.Data;
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

    public RecordingNotifier Notifier { get; } = new();

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17").Build();
    private WebApplicationFactory<Program>? _factory;

    public async ValueTask InitializeAsync()
    {
        await _db.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureTestServices(s => s.AddSingleton<IChangeNotifier>(Notifier));
            b.UseSetting("ConnectionStrings:Raf", _db.GetConnectionString());
            b.UseSetting("Jwt:SigningKey", Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
            b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
            b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
            b.UseSetting("Bootstrap:AdminMustChangePassword", "false");
            b.UseSetting("Confidential:LocalKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        });
        _ = _factory.Server; // arranca el host: migra y crea el admin
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public IServiceProvider Services => _factory!.Services;

    public HttpClient Anonymous() => _factory!.CreateClient();

    /// <summary>Cliente sin cabeceras propias que pasa por <paramref name="handler"/>, para simular fallos de red.</summary>
    public HttpClient ClientWith(DelegatingHandler handler) => _factory!.CreateDefaultClient(handler);

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

/// <summary>Registra los avisos recibidos, para comprobar que salen después de confirmar.</summary>
public sealed class RecordingNotifier : IChangeNotifier
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Guid Workspace, long Seq)> _calls = new();

    public IReadOnlyList<long> For(Guid workspace) => _calls.Where(c => c.Workspace == workspace).Select(c => c.Seq).ToList();

    public ValueTask NotifyAsync(Guid workspaceId, long changeSeq)
    {
        _calls.Enqueue((workspaceId, changeSeq));
        return ValueTask.CompletedTask;
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
