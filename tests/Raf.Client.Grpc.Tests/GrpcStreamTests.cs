using System.Text.Json;
using Raf.Client.Core.Grpc;

namespace Raf.Client.Grpc.Tests;

/// <summary>Las llamadas con streaming (Plan 10, etapa 3) contra el servidor de prueba.</summary>
public class GrpcStreamTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static GrpcTarget Target(Uri? address = null) =>
        new(address ?? TestGrpcServer.Address, false, [], TimeSpan.FromSeconds(30));

    /// <summary>Lo que llega por una llamada: los mensajes en orden y el final.</summary>
    private sealed class Recorder
    {
        public List<GrpcStreamMessage> Messages { get; } = [];
        public TaskCompletionSource<GrpcCallResult> Done { get; } = new();

        public Recorder(IGrpcStream stream)
        {
            stream.Received += m =>
            {
                lock (Messages) Messages.Add(m);
            };
            stream.Completed += r => Done.TrySetResult(r);
            stream.Start();
        }

        public IEnumerable<string> Names => Messages.Select(m => JsonDocument.Parse(m.Json!).RootElement.GetProperty("name").GetString()!);
    }

    [Fact]
    public async Task Server_streaming_delivers_every_message_and_the_trailers()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        await using var stream = await engine.StartStreamAsync(new GrpcCall(Target(), "raf.test.Echo", "Watch",
            """{ "name": "n", "values": [1, 2, 3] }"""));
        var recorder = new Recorder(stream);
        var result = await recorder.Done.Task.WaitAsync(Wait);

        Assert.Equal(GrpcMethodKind.ServerStreaming, stream.Kind);
        Assert.False(stream.CanSend);
        Assert.True(result.IsOk);
        Assert.Equal(["n 1", "n 2", "n 3"], recorder.Names);
        Assert.All(recorder.Messages, m => Assert.True(m.Size > 0));
        Assert.Contains(KeyValuePair.Create("x-total", "3"), result.Trailers);
    }

    [Fact]
    public async Task Client_streaming_answers_once_after_the_client_finishes()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        await using var stream = await engine.StartStreamAsync(new GrpcCall(Target(), "raf.test.Echo", "Upload", ""));
        var recorder = new Recorder(stream);
        var size = await stream.SendAsync("""{ "name": "a", "values": [1, 2] }""");
        await stream.SendAsync("""{ "name": "b", "values": [10] }""");
        Assert.True(stream.CanSend);
        await stream.CompleteSendingAsync();
        var result = await recorder.Done.Task.WaitAsync(Wait);

        Assert.True(size > 0);
        Assert.False(stream.CanSend);
        Assert.True(result.IsOk);
        var answer = JsonDocument.Parse(Assert.Single(recorder.Messages).Json!).RootElement;
        Assert.Equal("a,b", answer.GetProperty("name").GetString());
        Assert.Equal(13, answer.GetProperty("values")[0].GetInt32());
    }

    [Fact]
    public async Task Bidirectional_streaming_answers_each_message_and_reports_the_server_error()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        await using var stream = await engine.StartStreamAsync(new GrpcCall(Target(), "raf.test.Echo", "Chat", ""));
        var recorder = new Recorder(stream);
        await stream.SendAsync("""{ "name": "uno" }""");
        await stream.SendAsync("""{ "name": "dos" }""");
        await WaitUntil(() => recorder.Messages.Count == 2);
        await stream.SendAsync("""{ "name": "chau" }""");
        var result = await recorder.Done.Task.WaitAsync(Wait);

        Assert.Equal(GrpcMethodKind.DuplexStreaming, stream.Kind);
        Assert.Equal(["eco uno", "eco dos"], recorder.Names);
        Assert.Equal(("ABORTED", "me fui"), (result.StatusName, result.StatusDetail));
    }

    [Fact]
    public async Task Cancelling_ends_with_cancelled_and_bad_json_is_explained_before_sending()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        await using var endless = await engine.StartStreamAsync(new GrpcCall(Target(), "raf.test.Echo", "Watch", """{ "name": "infinito" }"""));
        var recorder = new Recorder(endless);
        await WaitUntil(() => recorder.Messages.Count >= 3);
        endless.Cancel();
        var result = await recorder.Done.Task.WaitAsync(Wait);
        await using var chat = await engine.StartStreamAsync(new GrpcCall(Target(), "raf.test.Echo", "Chat", ""));
        new Recorder(chat);
        var bad = await Assert.ThrowsAsync<GrpcEngineException>(() => chat.SendAsync("""{ "nope": 1 }"""));

        Assert.Equal("CANCELLED", result.StatusName);
        Assert.Contains("nope", bad.Message);
    }

    [Fact]
    public async Task Unary_and_streaming_are_opened_the_right_way()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        var notStreaming = await Assert.ThrowsAsync<GrpcEngineException>(() =>
            engine.StartStreamAsync(new GrpcCall(Target(), "raf.test.Echo", "Echo", "{}")));
        var notUnary = await Assert.ThrowsAsync<GrpcEngineException>(() =>
            engine.CallUnaryAsync(new GrpcCall(Target(), "raf.test.Echo", "Chat", "{}")));

        Assert.Equal(string.Format(CoreTexts.Grpc_NotStreaming, "raf.test.Echo/Echo"), notStreaming.Message);
        Assert.Equal(string.Format(CoreTexts.Grpc_NotUnary, "raf.test.Echo/Chat"), notUnary.Message);
    }

    [Fact]
    public async Task Streams_over_a_real_connection()
    {
        await using var server = await TestGrpcServer.StartAsync(network: true);
        using var engine = new GrpcEngine();

        await using var stream = await engine.StartStreamAsync(new GrpcCall(Target(server.NetworkAddress), "raf.test.Echo", "Chat", ""));
        var recorder = new Recorder(stream);
        await stream.SendAsync("""{ "name": "red" }""");
        await WaitUntil(() => recorder.Messages.Count == 1);
        await stream.CompleteSendingAsync();
        var result = await recorder.Done.Task.WaitAsync(Wait);

        Assert.True(result.IsOk);
        Assert.Equal(["eco red"], recorder.Names);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
