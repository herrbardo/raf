using Raf.Client.Core.Api;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Local;
using Raf.Client.Core.Session;
using Raf.Domain.Requests;

namespace Raf.Server.Tests;

/// <summary>El cliente web: <see cref="RequestRunner"/> enviando por el relay del servidor real.</summary>
public class RelayHandlerTests(RafApp app, RelayTarget target) : IClassFixture<RelayTarget>
{
    private static readonly RunOptions Defaults = new(TimeSpan.FromSeconds(30));

    private async Task<RequestRunner> RunnerAsync()
    {
        var (user, _) = await app.CreateActiveUserAsync();
        var http = app.ClientWith(new ClientVersionHandler(RafApp.MinClientVersion));
        var lockPath = Path.Combine(Path.GetTempPath(), "raf-relay-" + Guid.NewGuid().ToString("N"), "session.lock");
        var session = new SessionManager(http, new MemorySecureStore(), TimeProvider.System, lockPath);
        session.SetServer(new Uri("http://localhost/"));
        await session.LoginAsync(user.Email, "NuevaClave123");
        var api = new RafApiClient(http, session);
        return new RequestRunner(handlers: invalid => new RelayHandler(api, invalid));
    }

    private static RunInput Input(string url, string method = "GET", string? body = null) => new(
        RequestSpec.Empty with
        {
            Method = method,
            Url = url,
            Headers = [new KeyValueItem("X-Prueba", "valor")],
            Body = body is null ? BodySpec.None : new BodySpec(BodyType.Text, body),
        },
        null, VariableContext.Empty, Defaults);

    [Fact]
    public async Task Runs_through_the_relay_following_redirects_with_the_full_trace()
    {
        using var runner = await RunnerAsync();
        using var result = await runner.RunAsync(Input(target.Url("/redirect")));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("X-Prueba: valor", result.Body!.GetPreviewText());
        Assert.Contains(result.Headers, h => h is { Key: "X-Echo", Value: "1" });
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.Redirect);
        Assert.Equal(2, result.Events.Count(e => e.Kind == RunEventKind.Dns));
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.Connect && e.Title.Contains($":{target.Port}"));
    }

    [Fact]
    public async Task Sends_the_body_and_its_content_type()
    {
        using var runner = await RunnerAsync();
        using var result = await runner.RunAsync(Input(target.Url("/echo"), "POST", "hola relay"));

        var text = result.Body!.GetPreviewText();
        Assert.StartsWith("POST", text);
        Assert.Contains("Content-Type: text/plain; charset=utf-8", text);
        Assert.EndsWith("hola relay", text);
    }

    [Fact]
    public async Task Marks_bodies_cut_by_the_server()
    {
        using var runner = await RunnerAsync();
        using var result = await runner.RunAsync(Input(target.Url("/big")));

        Assert.Equal(RafApp.RelayMaxResponseBytes, result.Body!.Length);
        Assert.Contains(result.Headers, h => h is { Key: RelayHandler.TruncatedHeader, Value: "100000" });
    }

    [Fact]
    public async Task Blocked_destinations_fail_with_the_server_reason()
    {
        using var runner = await RunnerAsync();
        using var result = await runner.RunAsync(Input($"http://127.0.0.1:{target.Port}/echo"));

        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains("127.0.0.1", result.Error);
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.Error);
    }
}
