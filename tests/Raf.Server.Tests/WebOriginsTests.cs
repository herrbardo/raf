using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Raf.Contracts;
using Raf.Server.Infrastructure;

namespace Raf.Server.Tests;

/// <summary>CORS para el cliente web: el navegador tiene que poder leer también los errores.</summary>
public class WebOriginsTests(RafApp app)
{
    private static HttpRequestMessage Preflight(string origin, string path = "/auth/login")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-raf-client-version");
        return request;
    }

    private static string? AllowedOrigin(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;

    [Fact]
    public async Task Preflight_from_the_web_client_is_allowed()
    {
        var res = await app.Anonymous().SendAsync(Preflight(RafApp.WebOrigin));

        Assert.True(res.IsSuccessStatusCode, $"Llegó {(int)res.StatusCode}");
        Assert.Equal(RafApp.WebOrigin, AllowedOrigin(res));
        var headers = string.Join(",", res.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        Assert.Contains("x-raf-client-version", headers);
        Assert.Contains("authorization", headers);
    }

    [Fact]
    public async Task Other_origins_get_no_cors_headers()
    {
        var res = await app.Anonymous().SendAsync(Preflight("https://otro-sitio.test"));
        Assert.Null(AllowedOrigin(res));
    }

    [Fact]
    public async Task Upgrade_required_is_readable_from_the_browser()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(RafApp.AdminEmail, "x"), options: RafApp.Json),
        };
        request.Headers.Add("Origin", RafApp.WebOrigin);
        request.Headers.Add(RafHeaders.ClientVersion, "1.0.0");

        var res = await app.Anonymous().SendAsync(request);

        Assert.Equal(HttpStatusCode.UpgradeRequired, res.StatusCode);
        Assert.Equal(RafApp.WebOrigin, AllowedOrigin(res));
    }

    [Fact]
    public async Task Unauthorized_is_readable_from_the_browser()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Origin", RafApp.WebOrigin);

        var res = await app.Anonymous().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(RafApp.WebOrigin, AllowedOrigin(res));
    }

    [Fact]
    public async Task Hub_websocket_rejects_foreign_origins()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        var token = (await app.LoginRawAsync(user.Email, "NuevaClave123")).AccessToken;

        async Task ConnectAsync(string? origin)
        {
            var client = app.Server.CreateWebSocketClient();
            client.ConfigureRequest = r =>
            {
                r.Headers.Authorization = "Bearer " + token;
                if (origin is not null) r.Headers.Origin = origin;
            };
            using var socket = await client.ConnectAsync(new Uri("ws://localhost/hubs/changes"), CancellationToken.None);
        }

        await ConnectAsync(null); // la app de escritorio no manda Origin
        await ConnectAsync(RafApp.WebOrigin);
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("https://otro-sitio.test"));
    }

    [Fact]
    public void Origins_are_normalized_and_wildcards_rejected()
    {
        static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

        Assert.Equal(
            ["https://a.test", "https://b.test:8443", "http://localhost:5090"],
            WebOrigins.Read(Config(
                ("Cors:AllowedOrigins", "https://A.test/, https://b.test:8443"),
                ("Cors:AllowedOrigins:0", "http://localhost:5090"))));
        Assert.Empty(WebOrigins.Read(Config()));
        Assert.Equal(["https://raf-web.example.com"], WebOrigins.Read(Config(("Cors:AllowedOrigins:0", "raf-web.example.com"))));
        Assert.Throws<InvalidOperationException>(() => WebOrigins.Read(Config(("Cors:AllowedOrigins", "*"))));
        Assert.Throws<InvalidOperationException>(() => WebOrigins.Read(Config(("Cors:AllowedOrigins", "https://*.test"))));
        Assert.Throws<InvalidOperationException>(() => WebOrigins.Read(Config(("Cors:AllowedOrigins", "https://a.test/app"))));
    }
}
