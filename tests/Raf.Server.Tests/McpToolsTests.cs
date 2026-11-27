using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Raf.Client.Core.Api;
using Raf.Client.Core.History;
using Raf.Client.Core.Local;
using Raf.Client.Core.Localization;
using Raf.Client.Core.Mcp;
using Raf.Client.Core.Session;
using Raf.Client.Mcp;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Server.Tests;

/// <summary>Las herramientas del MCP (Plan 12), por el cliente del SDK contra el servidor real.</summary>
public sealed partial class McpToolsTests(RafApp app)
{
    private static readonly Uri Server = new("http://localhost/");

    /// <summary>Un MCP conectado con la sesión del admin, con las preferencias que el test cambia.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private McpServer? _server;

        public LocalSettings Settings { get; } = new();
        public McpClient Client { get; private set; } = null!;

        public MemorySecureStore Secure { get; } = new();
        public Guid UserId { get; private set; }
        public RunHistoryStore History { get; } = new(Path.Combine(Path.GetTempPath(), "raf-mcp-history-" + Guid.NewGuid().ToString("N")));

        public static async Task<Harness> StartAsync(RafApp app, bool connect = true, Func<bool, HttpMessageHandler>? handlers = null)
        {
            var harness = new Harness();
            var http = app.Anonymous();
            var store = harness.Secure;
            var dir = Path.Combine(Path.GetTempPath(), "raf-mcp-" + Guid.NewGuid().ToString("N"));
            var connection = new McpConnection(http, store, TimeProvider.System, dir);
            if (connect)
            {
                var appSession = new SessionManager(http, store, TimeProvider.System, Path.Combine(dir, "session.lock"));
                appSession.SetServer(Server);
                await appSession.LoginAsync(RafApp.AdminEmail, RafApp.AdminPassword);
                await connection.ConnectAsync(new RafApiClient(http, appSession), Server);
                harness.UserId = appSession.User!.Id;
            }

            var context = new RafMcpContext(connection, http, () => harness.Settings, store, harness.History,
                new Raf.Client.Core.Execution.RequestRunner(handlers: handlers));
            var options = RafMcpServer.CreateOptions(context, "test");
            var toServer = new Pipe();
            var toClient = new Pipe();
            harness._server = McpServer.Create(
                new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "raf"), options);
            _ = harness._server.RunAsync(harness._stop.Token);
            harness.Client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
            return harness;
        }

        public async Task<(bool IsError, string Text)> CallAsync(string tool, object? args = null)
        {
            var arguments = args is null
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
            var result = await Client.CallToolAsync(tool, arguments);
            return (result.IsError == true, string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        }

        public async Task<JsonElement> JsonAsync(string tool, object? args = null)
        {
            var (isError, text) = await CallAsync(tool, args);
            Assert.False(isError, text);
            return JsonDocument.Parse(text).RootElement;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            if (_server is not null) await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lists_the_tools_and_explains_what_is_missing()
    {
        await using var notConnected = await Harness.StartAsync(app, connect: false);
        var tools = await notConnected.Client.ListToolsAsync();
        Assert.Equal(
            [
                "create_app", "create_environment", "create_folder", "create_request", "create_sample",
                "delete_app", "delete_environment", "delete_folder", "delete_request", "delete_sample", "delete_variable",
                "get_app", "get_request", "get_tree", "list_environments", "list_variables", "list_workspaces", "run_request",
                "set_variable", "update_app", "update_folder", "update_request", "update_sample",
            ],
            tools.Select(t => t.Name).Order());
        Assert.All(tools.Where(t => t.Name.StartsWith("get_") || t.Name.StartsWith("list_")),
            t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.All(tools.Where(t => t.Name.StartsWith("delete_") || t.Name == "run_request"), t =>
            Assert.Equal((false, true), (t.ProtocolTool.Annotations!.ReadOnlyHint, t.ProtocolTool.Annotations.DestructiveHint)));
        Assert.All(tools.Where(t => t.Name.StartsWith("create_")),
            t => Assert.False(t.ProtocolTool.Annotations!.DestructiveHint));

        var forbidden = await notConnected.CallAsync("list_workspaces");
        Assert.True(forbidden.IsError);
        Assert.Contains(string.Format(CoreTexts.Mcp_NotAllowed, CoreTexts.Mcp_PermissionRead), forbidden.Text);

        notConnected.Settings.Mcp = new McpPermissions(Read: true);
        var unconnected = await notConnected.CallAsync("list_workspaces");
        Assert.True(unconnected.IsError);
        Assert.Contains(CoreTexts.Mcp_NotConnectedError, unconnected.Text);
    }

    [Fact]
    public async Task Reads_the_tree_requests_variables_and_apps_without_secret_values()
    {
        var admin = await app.AdminAsync();
        var name = "MCP " + Guid.NewGuid().ToString("N")[..8];
        var ws = await admin.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest(name));
        var w = $"/workspaces/{ws.Id}";
        var dev = await admin.PostOkAsync<EnvironmentDto>($"{w}/environments", new CreateEnvironmentRequest("dev"));
        var orders = await admin.PostOkAsync<AppDto>($"{w}/apps",
            new CreateAppRequest("Orders", 0, Auth: new AuthSpec(AuthType.Bearer, Token: "{{token}}")));
        await admin.PostOkAsync<AppDto>($"{w}/apps", new CreateAppRequest("Users", 1));
        var folder = await admin.PostOkAsync<FolderDto>($"{w}/folders", new CreateFolderRequest(orders.Id, "Pedidos"));
        var spec = RequestSpec.Empty with { Method = "GET", Url = "{{baseUrl}}/pedidos" };
        var list = await admin.PostOkAsync<RequestDto>($"{w}/requests", new CreateRequestRequest(orders.Id, "Listar", spec, folder.Id));
        var sample = await admin.PostOkAsync<SampleDto>($"{w}/samples",
            new CreateSampleRequest(list.Id, "Página 2", SampleOverrides.Empty with { Url = "{{baseUrl}}/pedidos?page=2" }));
        await admin.PostOkAsync<VariableDto>($"{w}/variables", new CreateVariableRequest("baseUrl", "https://orders.example.com", dev.Id));
        await admin.PostOkAsync<VariableDto>($"{w}/variables", new CreateVariableRequest("token", "valor-secreto", IsSecret: true));

        await using var mcp = await Harness.StartAsync(app);
        mcp.Settings.Mcp = new McpPermissions(Read: true);
        mcp.Settings.ActiveEnvironments[ws.Id] = dev.Id;

        var workspaces = await mcp.JsonAsync("list_workspaces");
        Assert.Contains(workspaces.EnumerateArray(), x => x.GetProperty("name").GetString() == name && x.GetProperty("role").GetString() == "Owner");

        var environments = await mcp.JsonAsync("list_environments", new { workspace = name });
        Assert.True(Assert.Single(environments.EnumerateArray()).GetProperty("active").GetBoolean());

        var tree = await mcp.JsonAsync("get_tree", new { workspace = ws.Id.ToString(), app = "orders" });
        var app0 = Assert.Single(tree.EnumerateArray());
        var request = Assert.Single(Assert.Single(app0.GetProperty("folders").EnumerateArray()).GetProperty("requests").EnumerateArray());
        Assert.Equal(("Listar", "http", "GET", "{{baseUrl}}/pedidos"),
            (request.GetProperty("name").GetString(), request.GetProperty("kind").GetString(),
             request.GetProperty("method").GetString(), request.GetProperty("url").GetString()));
        Assert.Equal("Página 2", Assert.Single(request.GetProperty("samples").EnumerateArray()).GetProperty("name").GetString());

        var fromSample = await mcp.JsonAsync("get_request", new { workspace = name, sample = sample.Id });
        Assert.Equal("{{baseUrl}}/pedidos?page=2", fromSample.GetProperty("effective").GetProperty("url").GetString());
        var fromRequest = await mcp.JsonAsync("get_request", new { workspace = name, request = list.Id });
        Assert.Equal("GET", fromRequest.GetProperty("spec").GetProperty("method").GetString());
        Assert.True((await mcp.CallAsync("get_request", new { workspace = name })).IsError);

        var (_, variablesText) = await mcp.CallAsync("list_variables", new { workspace = name });
        Assert.DoesNotContain("valor-secreto", variablesText);
        var variables = JsonDocument.Parse(variablesText).RootElement.EnumerateArray().ToList();
        var token = Assert.Single(variables, v => v.GetProperty("key").GetString() == "token");
        Assert.True(token.GetProperty("secret").GetBoolean());
        Assert.False(token.TryGetProperty("value", out _));
        var baseUrl = Assert.Single(variables, v => v.GetProperty("key").GetString() == "baseUrl");
        Assert.Equal("dev", baseUrl.GetProperty("environment").GetString());
        Assert.False(baseUrl.TryGetProperty("app", out _));

        var appInfo = await mcp.JsonAsync("get_app", new { workspace = name, app = "Orders" });
        Assert.Equal("{{token}}", appInfo.GetProperty("auth").GetProperty("token").GetString());

        var missing = await mcp.CallAsync("get_tree", new { workspace = name, app = "Nope" });
        Assert.True(missing.IsError);
        Assert.Contains(string.Format(CoreTexts.Mcp_NotFound, CoreTexts.Mcp_KindApp, "Nope"), missing.Text);
    }
}
