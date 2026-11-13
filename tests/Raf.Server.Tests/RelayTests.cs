using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Raf.Contracts;
using Raf.Server.Relay;

namespace Raf.Server.Tests;

/// <summary>Un servidor HTTP real en localhost, destino de los requests del relay.</summary>
public sealed class RelayTarget : IAsyncLifetime
{
    private WebApplication? _app;

    public int Port { get; private set; }

    public string Url(string path) => $"http://localhost:{Port}{path}";

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        // Sin reutilizar conexiones: cada envío del relay abre una y la informa.
        _app.Use((ctx, next) =>
        {
            ctx.Response.Headers.Connection = "close";
            return next(ctx);
        });
        _app.Map("/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var headers = string.Join("\n", ctx.Request.Headers.Select(h => $"{h.Key}: {h.Value}"));
            ctx.Response.Headers["X-Echo"] = "1";
            return Results.Text($"{ctx.Request.Method}\n{headers}\n\n{body}");
        });
        _app.MapGet("/redirect", () => Results.Redirect("/echo"));
        _app.MapGet("/big", () => Results.Text(new string('a', 100_000)));
        await _app.StartAsync();
        Port = new Uri(_app.Urls.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

public class RelayTests(RafApp app, RelayTarget target) : IClassFixture<RelayTarget>
{
    private static RelayRequest Get(string url, params RelayHeader[] headers) => new("GET", url, headers, null, 30);

    private static async Task<RelayResponse> OkAsync(HttpResponseMessage res)
    {
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<RelayResponse>(RafApp.Json))!;
    }

    private static async Task AssertFailureAsync(HttpResponseMessage res, HttpStatusCode status, string kind)
    {
        await res.EnsureStatusAsync(status);
        var problem = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>(RafApp.Json);
        Assert.Equal(kind, problem![RelayErrors.Extension].ToString());
    }

    private static string Text(RelayResponse res) => Encoding.UTF8.GetString(Convert.FromBase64String(res.Body));

    [Fact]
    public async Task Requires_a_session()
    {
        var res = await app.Anonymous().PostJsonAsync("/relay", Get(target.Url("/echo")));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Sends_the_request_and_returns_the_response_with_the_connection()
    {
        var (_, client) = await app.CreateActiveUserAsync();
        var request = new RelayRequest("POST", target.Url("/echo?q=1"),
            [new RelayHeader("X-Prueba", "valor"), new RelayHeader("Content-Type", "text/plain")],
            Convert.ToBase64String(Encoding.UTF8.GetBytes("hola relay")), 30);

        var res = await OkAsync(await client.PostJsonAsync("/relay", request));

        Assert.Equal(200, res.StatusCode);
        Assert.False(res.Truncated);
        var text = Text(res);
        Assert.StartsWith("POST", text);
        Assert.Contains("X-Prueba: valor", text);
        Assert.Contains("Content-Type: text/plain", text);
        Assert.EndsWith("hola relay", text);
        Assert.Contains(res.Headers, h => h is { Name: "X-Echo", Value: "1" });
        Assert.NotNull(res.Connection);
        Assert.Equal("localhost", res.Connection.Host);
        Assert.EndsWith($":{target.Port}", res.Connection.Remote);
    }

    [Fact]
    public async Task Does_not_follow_redirects()
    {
        var (_, client) = await app.CreateActiveUserAsync();
        var res = await OkAsync(await client.PostJsonAsync("/relay", Get(target.Url("/redirect"))));

        Assert.Equal(302, res.StatusCode);
        Assert.Contains(res.Headers, h => h.Name == "Location" && h.Value.EndsWith("/echo"));
    }

    [Fact]
    public async Task Cuts_bodies_over_the_limit()
    {
        var (_, client) = await app.CreateActiveUserAsync();
        var res = await OkAsync(await client.PostJsonAsync("/relay", Get(target.Url("/big"))));

        Assert.True(res.Truncated);
        Assert.Equal(RafApp.RelayMaxResponseBytes, Convert.FromBase64String(res.Body).Length);
        Assert.Equal(100_000, res.BodyLength);
    }

    [Theory]
    [InlineData("http://127.0.0.1:{port}/echo")]
    [InlineData("http://[::1]:{port}/echo")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.0.1/")]
    public async Task Blocks_internal_addresses(string url)
    {
        var (_, client) = await app.CreateActiveUserAsync();
        var res = await client.PostJsonAsync("/relay", Get(url.Replace("{port}", target.Port.ToString())));
        await AssertFailureAsync(res, HttpStatusCode.Forbidden, RelayErrors.Blocked);
    }

    [Fact]
    public async Task Reports_unreachable_destinations()
    {
        var (_, client) = await app.CreateActiveUserAsync();
        var res = await client.PostJsonAsync("/relay", Get("http://localhost:1/"));
        await AssertFailureAsync(res, HttpStatusCode.BadGateway, RelayErrors.Unreachable);
    }

    [Theory]
    [InlineData("GET", "ftp://example.com/")]
    [InlineData("GET", "/relativa")]
    [InlineData("NO VALE", "http://localhost/")]
    public async Task Rejects_invalid_requests(string method, string url)
    {
        var (_, client) = await app.CreateActiveUserAsync();
        var res = await client.PostJsonAsync("/relay", new RelayRequest(method, url, [], null, 30));
        await AssertFailureAsync(res, HttpStatusCode.BadRequest, RelayErrors.Invalid);
    }

    [Fact]
    public async Task Rejects_bodies_over_the_limit()
    {
        var (_, client) = await app.CreateActiveUserAsync();
        var body = Convert.ToBase64String(new byte[RafApp.RelayMaxRequestBytes + 1]);
        var res = await client.PostJsonAsync("/relay", new RelayRequest("POST", target.Url("/echo"), [], body, 30));
        await AssertFailureAsync(res, HttpStatusCode.RequestEntityTooLarge, RelayErrors.TooLarge);
    }

    [Fact]
    public async Task Limits_requests_per_user()
    {
        var (_, client) = await app.CreateActiveUserAsync();
        // Todos juntos, para que no crucen el cambio de ventana del límite.
        var responses = await Task.WhenAll(Enumerable.Range(0, RafApp.RelayPerMinute + 5)
            .Select(_ => client.PostJsonAsync("/relay", Get(target.Url("/echo")))));

        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(RafApp.RelayPerMinute, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

        var (_, other) = await app.CreateActiveUserAsync(); // otro usuario tiene su propio cupo
        await OkAsync(await other.PostJsonAsync("/relay", Get(target.Url("/echo"))));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.20.30.40", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2002:a00:1::1", false)]
    public void Address_policy(string address, bool isPublic) =>
        Assert.Equal(isPublic, RelayAddressPolicy.IsPublic(IPAddress.Parse(address)));
}
