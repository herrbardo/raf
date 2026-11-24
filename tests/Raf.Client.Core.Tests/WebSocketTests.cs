using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Localization;
using Raf.Client.Core.WebSockets;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Tests;

public class WebSocketTests
{
    /// <summary>
    /// Un servidor WebSocket real en un puerto libre: /echo devuelve cada mensaje (y manda uno binario si recibe
    /// "binario"), /bye cierra apenas conecta y /private exige Authorization.
    /// </summary>
    private static async Task<(WebApplication App, Uri Base)> StartServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/echo", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(context.WebSockets.WebSocketRequestedProtocols.FirstOrDefault());
            var buffer = new byte[1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "chau", CancellationToken.None);
                    return;
                }
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (text == "binario") await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
                else await socket.SendAsync(Encoding.UTF8.GetBytes($"eco: {text} ({context.Request.Query["q"]})"), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });
        app.Map("/bye", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "no te quiero", CancellationToken.None);
        });
        app.Map("/private", async context =>
        {
            if (context.Request.Headers.Authorization != "Bearer abc")
            {
                context.Response.StatusCode = 401;
                context.Response.Headers["X-Motivo"] = "sin token";
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(Encoding.UTF8.GetBytes("bienvenido"), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.ReceiveAsync(new byte[16], CancellationToken.None);
        });
        await app.StartAsync();
        var http = new Uri(app.Urls.First());
        return (app, new Uri($"ws://{http.Authority}/"));
    }

    private static WebSocketConnectOptions Options(Uri uri, params (string Name, string Value)[] headers) =>
        new(uri, headers.Select(h => KeyValuePair.Create(h.Name, h.Value)).ToList(), [], false, TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Sends_and_receives_text_and_binary_and_closes_cleanly()
    {
        var (app, root) = await StartServerAsync();
        await using var _ = app;
        var received = new List<WebSocketFrame>();
        var closed = new TaskCompletionSource<WebSocketClosed>();
        var two = new TaskCompletionSource();

        await using var session = await WebSocketSession.ConnectAsync(Options(new Uri(root, "echo?q=1")));
        session.Received += f =>
        {
            lock (received) received.Add(f);
            if (received.Count == 2) two.TrySetResult();
        };
        session.Closed += c => closed.TrySetResult(c);
        session.Start();

        var sent = await session.SendTextAsync("hola ñ");
        await session.SendTextAsync("binario");
        await two.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.CloseAsync();

        Assert.Equal(101, session.Status);
        Assert.Equal((WebSocketDirection.Sent, "hola ñ", 7L), (sent.Direction, sent.Text, sent.Size));
        Assert.Equal("eco: hola ñ (1)", received[0].Text);
        Assert.Equal((true, "AQID", 3L), (received[1].IsBinary, received[1].Text, received[1].Size));
        var end = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(end.ByServer);
        Assert.Null(end.Error);
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task Reports_when_the_server_closes()
    {
        var (app, root) = await StartServerAsync();
        await using var _ = app;
        var closed = new TaskCompletionSource<WebSocketClosed>();

        await using var session = await WebSocketSession.ConnectAsync(Options(new Uri(root, "bye")));
        session.Closed += c => closed.TrySetResult(c);
        session.Start();

        var end = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(((int?)WebSocketCloseStatus.PolicyViolation, "no te quiero", true), (end.Status, end.Description, end.ByServer));
    }

    [Fact]
    public async Task Explains_a_rejected_handshake_and_accepts_the_right_headers()
    {
        var (app, root) = await StartServerAsync();
        await using var _ = app;
        var welcome = new TaskCompletionSource<WebSocketFrame>();

        var rejected = await Assert.ThrowsAsync<WebSocketConnectException>(() => WebSocketSession.ConnectAsync(Options(new Uri(root, "private"))));
        await using var session = await WebSocketSession.ConnectAsync(Options(new Uri(root, "private"), ("Authorization", "Bearer abc")));
        session.Received += f => welcome.TrySetResult(f);
        session.Start();

        Assert.Equal(401, rejected.Status);
        Assert.Equal(string.Format(CoreTexts.Ws_Rejected, 401), rejected.Message);
        Assert.Contains(rejected.Headers, h => h.Key.Equals("X-Motivo", StringComparison.OrdinalIgnoreCase) && h.Value == "sin token");
        Assert.Equal("bienvenido", (await welcome.Task.WaitAsync(TimeSpan.FromSeconds(10))).Text);
    }

    [Fact]
    public async Task Explains_a_closed_port()
    {
        var error = await Assert.ThrowsAsync<WebSocketConnectException>(() =>
            WebSocketSession.ConnectAsync(Options(new Uri("ws://127.0.0.1:1/"))));

        Assert.Null(error.Status);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData("wss://example.com/socket", "wss://example.com/socket")]
    [InlineData("https://example.com/socket", "wss://example.com/socket")]
    [InlineData("WS://localhost:8080", "ws://localhost:8080/")]
    public void Builds_the_handshake_url(string url, string expected) =>
        Assert.Equal(new Uri(expected), RequestRunner.WebSocketHandshake(RequestSpec.EmptyWebSocket with { Url = url }).Uri);

    [Fact]
    public void Adds_query_and_auth_to_the_handshake()
    {
        var spec = RequestSpec.EmptyWebSocket with
        {
            Url = "wss://example.com/ws?a=1",
            QueryParams = [new("b", "dos dos")],
            Headers = [new("X-Uno", "1"), new("authorization", "se reemplaza")],
            Auth = new AuthSpec(AuthType.Bearer, "t"),
        };
        var apiKey = spec with { Auth = new AuthSpec(AuthType.ApiKey, ApiKeyName: "key", ApiKeyValue: "k", ApiKeyIn: ApiKeyLocation.Query) };

        var (uri, headers) = RequestRunner.WebSocketHandshake(spec);

        Assert.Equal("wss://example.com/ws?a=1&b=dos%20dos", uri.AbsoluteUri);
        Assert.Equal([KeyValuePair.Create("X-Uno", "1"), KeyValuePair.Create("Authorization", "Bearer t")], headers);
        Assert.EndsWith("&key=k", RequestRunner.WebSocketHandshake(apiKey).Uri.AbsoluteUri);
        Assert.Throws<RequestBuildException>(() => RequestRunner.WebSocketHandshake(spec with { Url = "ftp://example.com" }));
    }
}
