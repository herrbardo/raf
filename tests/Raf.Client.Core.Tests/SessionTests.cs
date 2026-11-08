using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using Raf.Client.Core.Api;
using Raf.Client.Core.Execution;
using Raf.Client.Core.History;
using Raf.Client.Core.Local;
using Raf.Client.Core.Session;
using Raf.Contracts;

namespace Raf.Client.Core.Tests;

/// <summary>Simula <c>/auth/refresh</c> como el servidor real: reusar un token revocado cierra todas las sesiones.</summary>
internal sealed class FakeAuthServer : HttpMessageHandler
{
    private readonly object _lock = new();
    private string _validRefresh = "r0";
    private int _generation;

    public int RefreshCalls;
    public bool TheftDetected;
    public bool Rejected;
    public TaskCompletionSource? Gate;
    public Func<string?, HttpResponseMessage>? Api;

    public static TokenResponse Tokens(int n, DateTimeOffset now, TimeSpan accessLife) => new(
        $"a{n}", now + accessLife, $"r{n}", now.AddDays(30), new UserDto(Guid.Empty, "u@test.local", "U", false, false, false));

    public DateTimeOffset Now = DateTimeOffset.UtcNow;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal))
            return Api!(request.Headers.Authorization?.Parameter);

        var body = await request.Content!.ReadFromJsonAsync<RefreshRequest>(ClientJson.Options, CancellationToken.None);
        Interlocked.Increment(ref RefreshCalls);
        if (Gate is { } gate) await gate.Task;

        lock (_lock)
        {
            if (Rejected) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (body!.RefreshToken != _validRefresh)
            {
                TheftDetected = true;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            _generation++;
            _validRefresh = $"r{_generation}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Tokens(_generation, Now, TimeSpan.FromMinutes(15)), options: ClientJson.Options),
            };
        }
    }
}

public sealed class SessionTests : IDisposable
{
    private static readonly Uri Server = new("https://raf.test/sub/");

    private readonly string _dir = Directory.CreateTempSubdirectory("raf-tests").FullName;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly MemorySecureStore _store = new();
    private readonly FakeAuthServer _auth = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SessionManager NewSession()
    {
        var session = new SessionManager(new HttpClient(_auth), _store, _clock, Path.Combine(_dir, "lock"));
        session.SetServer(Server);
        return session;
    }

    private SessionManager LoggedInWithExpiredToken()
    {
        _auth.Now = _clock.GetUtcNow();
        var session = NewSession();
        session.Adopt(FakeAuthServer.Tokens(0, _clock.GetUtcNow(), TimeSpan.FromSeconds(-1)));
        return session;
    }

    [Fact]
    public async Task Renovacion_concurrente_hace_un_solo_refresh()
    {
        var session = LoggedInWithExpiredToken();
        _auth.Gate = new TaskCompletionSource();

        var calls = Enumerable.Range(0, 20).Select(_ => Task.Run(() => session.GetAccessTokenAsync())).ToList();
        await Task.Delay(100);
        _auth.Gate.SetResult();
        var tokens = await Task.WhenAll(calls);

        Assert.Equal(1, _auth.RefreshCalls);
        Assert.False(_auth.TheftDetected);
        Assert.All(tokens, t => Assert.Equal("a1", t));
    }

    [Fact]
    public async Task Cancelar_a_quien_espera_no_pierde_el_token_rotado()
    {
        var session = LoggedInWithExpiredToken();
        _auth.Gate = new TaskCompletionSource();

        using var cts = new CancellationTokenSource();
        var waiting = session.GetAccessTokenAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        _auth.Gate.SetResult();

        for (var i = 0; i < 100 && _auth.RefreshCalls == 0; i++) await Task.Delay(10);
        Assert.Equal("a1", await session.GetAccessTokenAsync());
        Assert.False(_auth.TheftDetected);
    }

    [Fact]
    public async Task Dos_instancias_de_la_app_no_reutilizan_el_refresh_token()
    {
        var first = LoggedInWithExpiredToken();
        var second = NewSession();
        Assert.True(second.TryRestore());

        var tokens = await Task.WhenAll(
            Task.Run(() => first.GetAccessTokenAsync()), Task.Run(() => second.GetAccessTokenAsync()));

        Assert.Equal(1, _auth.RefreshCalls);
        Assert.False(_auth.TheftDetected);
        Assert.Equal(tokens[0], tokens[1]);
    }

    [Fact]
    public async Task Sesion_expirada_en_el_servidor_avisa_y_borra_los_tokens()
    {
        var session = LoggedInWithExpiredToken();
        _auth.Rejected = true;
        var expired = 0;
        session.SessionExpired += () => expired++;

        await Assert.ThrowsAsync<SessionExpiredException>(() => session.GetAccessTokenAsync());

        Assert.Equal(1, expired);
        Assert.Null(session.User);
        Assert.False(NewSession().TryRestore());
    }

    [Fact]
    public async Task Rechazo_401_renueva_y_reintenta_una_vez()
    {
        var session = NewSession();
        _auth.Now = _clock.GetUtcNow();
        session.Adopt(FakeAuthServer.Tokens(0, _clock.GetUtcNow(), TimeSpan.FromMinutes(10)));
        _auth.Api = token => token == "a1"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { 1, 2 }) }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var api = new RafApiClient(new HttpClient(_auth), session);
        var result = await api.GetAsync<int[]>("x");
        Assert.Equal(new[] { 1, 2 }, result);
        Assert.Equal(1, _auth.RefreshCalls);
    }

    [Fact]
    public async Task Respeta_el_subpath_del_servidor()
    {
        var session = NewSession();
        session.Adopt(FakeAuthServer.Tokens(0, _clock.GetUtcNow(), TimeSpan.FromMinutes(10)));
        Uri? seen = null;
        var capture = new CapturingHandler(r => seen = r.RequestUri);

        await new RafApiClient(new HttpClient(capture), session).GetAsync<int[]>("workspaces");
        Assert.Equal("https://raf.test/sub/workspaces", seen!.ToString());
    }

    private sealed class CapturingHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<int>()) });
        }
    }

    [Theory]
    [InlineData("https://dominio.test/raf", "https://dominio.test/raf/")]
    [InlineData("https://dominio.test/raf/", "https://dominio.test/raf/")]
    [InlineData("dominio.test", "https://dominio.test/")]
    [InlineData("http://localhost:5000", "http://localhost:5000/")]
    public void Normaliza_la_url_del_servidor(string input, string expected) =>
        Assert.Equal(expected, ServerUrl.Normalize(input)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("ftp://dominio.test")]
    public void Rechaza_urls_invalidas(string input) => Assert.Null(ServerUrl.Normalize(input));

    [Fact]
    public async Task Historial_es_por_usuario_y_devuelve_lo_mas_reciente_primero()
    {
        var history = new RunHistoryStore(_dir);
        var (ana, beto) = (Guid.NewGuid(), Guid.NewGuid());
        RunHistoryEntry Entry(string name) => new(DateTimeOffset.UtcNow, null, null, name, "GET", "u", RunOutcome.Completed, 200, 1, 1, null);

        await history.AddAsync(ana, Entry("uno"));
        await history.AddAsync(ana, Entry("dos"));
        await history.AddAsync(beto, Entry("otro"));

        var entries = await history.GetAsync(ana);
        Assert.Equal(new[] { "dos", "uno" }, entries.Select(e => e.Name));
    }
}
