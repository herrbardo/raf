using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Raf.Client.Core.Sync;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Server.Tests;

/// <summary>Avisos por SignalR contra el servidor real, pasando por LISTEN/NOTIFY de Postgres.</summary>
public class RealtimeTests(RafApp app)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private async Task<(UserDto User, HttpClient Client, string Token)> NewUserAsync()
    {
        var (user, client) = await app.CreateActiveUserAsync();
        var token = (await app.LoginRawAsync(user.Email, "NuevaClave123")).AccessToken;
        return (user, client, token);
    }

    /// <summary>WebSocket del servidor de pruebas, con el token en la cabecera.</summary>
    private void UseTestSocket(HttpConnectionOptions o, string token)
    {
        o.Transports = HttpTransportType.WebSockets;
        o.SkipNegotiation = true;
        o.WebSocketFactory = async (ctx, ct) =>
        {
            var client = app.Server.CreateWebSocketClient();
            client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
            return await client.ConnectAsync(ctx.Uri, ct);
        };
    }

    private async Task<HubConnection> ConnectAsync(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri("http://localhost/hubs/changes"), o => UseTestSocket(o, token))
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task Members_get_notified_when_their_workspace_changes()
    {
        var (_, owner, ownerToken) = await NewUserAsync();
        var ws = await owner.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Tiempo real"));
        await using var connection = await ConnectAsync(ownerToken);

        var changed = new TaskCompletionSource<(Guid Ws, long Seq)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<Guid, long>("Changed", (w, seq) => changed.TrySetResult((w, seq)));
        Assert.Equal([ws.Id], await connection.InvokeAsync<Guid[]>("Watch", new[] { ws.Id }));

        var created = await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("Nueva"));

        var (gotWs, gotSeq) = await changed.Task.WaitAsync(Wait);
        Assert.Equal(ws.Id, gotWs);
        Assert.Equal(created.ChangeSeq, gotSeq);
    }

    [Fact]
    public async Task Watch_ignores_workspaces_of_other_users()
    {
        var (_, owner, _) = await NewUserAsync();
        var ws = await owner.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Ajeno"));
        var (_, _, strangerToken) = await NewUserAsync();
        await using var stranger = await ConnectAsync(strangerToken);

        var notified = false;
        stranger.On<Guid, long>("Changed", (_, _) => notified = true);
        Assert.Empty(await stranger.InvokeAsync<Guid[]>("Watch", new[] { ws.Id }));

        await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("Privada"));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(notified);
    }

    [Fact]
    public async Task Connections_require_a_valid_token()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri("http://localhost/hubs/changes"), o => UseTestSocket(o, "no-es-un-token"))
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task A_user_added_to_a_workspace_is_asked_to_refresh()
    {
        var (_, owner, _) = await NewUserAsync();
        var ws = await owner.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Sumarse"));
        var (newcomer, _, token) = await NewUserAsync();
        await using var connection = await ConnectAsync(token);

        var refresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On("Refresh", () => refresh.TrySetResult());

        await owner.PostOkAsync<MemberDto>($"/workspaces/{ws.Id}/members", new AddMemberRequest(newcomer.Id, WorkspaceRole.Viewer));
        await refresh.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Disabling_a_user_closes_their_open_connections()
    {
        var (user, _, token) = await NewUserAsync();
        await using var connection = await ConnectAsync(token);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var admin = await app.AdminAsync();
        await (await admin.PostAsync($"/admin/users/{user.Id}/disable", null)).EnsureStatusAsync(System.Net.HttpStatusCode.OK);

        await closed.Task.WaitAsync(Wait);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task Client_subscriber_fires_on_remote_changes_without_polling()
    {
        var (_, owner, ownerToken) = await NewUserAsync();
        var ws = await owner.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Suscriptor"));

        // Polling de respaldo muy lento: si el aviso llega, es por el WebSocket.
        using var subscriber = new RealtimeChangeSubscriber(
            new Uri("http://localhost/"), () => Task.FromResult<string?>(ownerToken), TimeProvider.System,
            TimeSpan.FromHours(1), o => UseTestSocket(o, ownerToken));
        var fired = 0;
        subscriber.ChangeAvailable += () => Interlocked.Increment(ref fired);
        subscriber.Watch([ws.Id]);
        subscriber.Start();

        var deadline = DateTime.UtcNow + Wait;
        while (!subscriber.IsConnected && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(subscriber.IsConnected);
        await Task.Delay(300); // termina de enviar la lista de workspaces
        var before = Volatile.Read(ref fired);

        await owner.PostOkAsync<AppDto>($"/workspaces/{ws.Id}/apps", new CreateAppRequest("Remota"));
        while (Volatile.Read(ref fired) == before && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(Volatile.Read(ref fired) > before);
    }
}
