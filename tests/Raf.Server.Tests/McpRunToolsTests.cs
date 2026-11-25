using System.Net;
using System.Text;
using System.Text.Json;
using Raf.Client.Core.History;
using Raf.Client.Core.Local;
using Raf.Client.Core.Localization;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Server.Tests;

/// <summary><c>run_request</c> (Plan 12, etapa 3), con un handler falso en lugar de la red.</summary>
public sealed partial class McpToolsTests
{
    private const string ApiBase = "https://api.test";
    private const string Secret = "s3cr3t-valor";
    private const string Token = "tok-123";
    private static readonly byte[] Binary = [0, 1, 2, 255];

    /// <summary>El login devuelve un token; /echo devuelve lo que recibió; /bin, bytes.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            if (path == "/token")
            {
                res.Content = new StringContent($$"""{ "access_token": "{{Token}}" }""", Encoding.UTF8, "application/json");
            }
            else if (path == "/bin")
            {
                res.Content = new ByteArrayContent(Binary);
                res.Content.Headers.ContentType = new("application/octet-stream");
            }
            else
            {
                var echo = new
                {
                    id = "p1",
                    clave = request.Headers.TryGetValues("X-Clave", out var c) ? c.Single() : null,
                    auth = request.Headers.Authorization?.ToString(),
                };
                res.Content = new StringContent(JsonSerializer.Serialize(echo), Encoding.UTF8, "application/json");
            }
            return Task.FromResult(res);
        }
    }

    [Fact]
    public async Task Runs_a_request_with_login_secrets_masked_session_and_history()
    {
        var admin = await app.AdminAsync();
        var name = "MCP run " + Guid.NewGuid().ToString("N")[..8];
        var ws = await admin.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest(name));
        var w = $"/workspaces/{ws.Id}";
        var dev = await admin.PostOkAsync<EnvironmentDto>($"{w}/environments", new CreateEnvironmentRequest("dev"));
        var login = await admin.PostOkAsync<LoginDto>($"{w}/logins", new CreateLoginRequest("Login",
            AppLogin.Default with { Enabled = true, Request = RequestSpec.Empty with { Method = "POST", Url = "{{baseUrl}}/token" }, Expiry = TokenExpiry.None }));
        var orders = await admin.PostOkAsync<AppDto>($"{w}/apps",
            new CreateAppRequest("Orders", Auth: new AuthSpec(AuthType.Bearer, Token: "{{token}}"), LoginId: login.Id));
        await admin.PostOkAsync<VariableDto>($"{w}/variables", new CreateVariableRequest("baseUrl", ApiBase, dev.Id));
        await admin.PostOkAsync<VariableDto>($"{w}/variables", new CreateVariableRequest("clave", Secret, IsSecret: true, AppId: orders.Id));
        var spec = RequestSpec.Empty with
        {
            Method = "GET",
            Url = "{{baseUrl}}/echo",
            Headers = [new KeyValueItem("X-Clave", "{{clave}}")],
            Auth = AuthSpec.Inherit,
            Extractions = [new ResponseExtraction("pedido", ExtractionSource.Body, "$.id")],
        };
        var echo = await admin.PostOkAsync<RequestDto>($"{w}/requests", new CreateRequestRequest(orders.Id, "Eco", spec));
        var bin = await admin.PostOkAsync<SampleDto>($"{w}/samples",
            new CreateSampleRequest(echo.Id, "Binario", SampleOverrides.Empty with { Url = "{{baseUrl}}/bin" }));

        await using var mcp = await Harness.StartAsync(app, handlers: _ => new FakeApi());
        mcp.Settings.Mcp = new McpPermissions(Read: true);
        mcp.Settings.ActiveEnvironments[ws.Id] = dev.Id;

        var forbidden = await mcp.CallAsync("run_request", new { workspace = name, request = echo.Id });
        Assert.True(forbidden.IsError);
        Assert.Contains(string.Format(CoreTexts.Mcp_NotAllowed, CoreTexts.Mcp_PermissionRun), forbidden.Text);

        mcp.Settings.Mcp = new McpPermissions(Read: true, Run: true);
        var (isError, text) = await mcp.CallAsync("run_request", new { workspace = name, request = echo.Id });
        Assert.False(isError, text);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(Token, text);
        var result = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("Completed", 200), (result.GetProperty("outcome").GetString(), result.GetProperty("status").GetInt32()));
        var body = JsonDocument.Parse(result.GetProperty("body").GetString()!).RootElement;
        Assert.Equal(("***", "Bearer ***"), (body.GetProperty("clave").GetString(), body.GetProperty("auth").GetString()));
        Assert.Equal(["pedido"], result.GetProperty("extracted").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(result.GetProperty("console").EnumerateArray(),
            e => e.GetProperty("title").GetString() == string.Format(CoreTexts.Login_TokenSaved, "token"));

        // Como la pestaña: el token y la extracción quedan en las variables de sesión que comparte con la app.
        var session = SessionVariableStore.For(mcp.Secure, new Uri("http://localhost/"), mcp.UserId).Get(ws.Id, dev.Id);
        Assert.Equal((Token, "p1"), (session["token"], session["pedido"]));
        var history = Assert.Single(await mcp.History.GetAsync(mcp.UserId));
        Assert.Equal(("Eco", RunHistoryEntry.McpSource, 200), (history.Name, history.Source, history.StatusCode));
        Assert.Equal(ApiBase + "/echo", history.Url);

        // Un sample con body binario: entero, en base64.
        var binary = await mcp.JsonAsync("run_request", new { workspace = name, sample = bin.Id, environment = "dev" });
        Assert.Equal(Convert.ToBase64String(Binary), binary.GetProperty("bodyBase64").GetString());
        Assert.False(binary.TryGetProperty("body", out _));

        var grpc = await admin.PostOkAsync<RequestDto>($"{w}/requests",
            new CreateRequestRequest(orders.Id, "gRPC", RequestSpec.EmptyGrpc with { Grpc = new GrpcSpec("a.B", "C") }));
        var onlyHttp = await mcp.CallAsync("run_request", new { workspace = name, request = grpc.Id });
        Assert.True(onlyHttp.IsError);
        Assert.Contains(CoreTexts.Mcp_OnlyHttp, onlyHttp.Text);
    }
}
