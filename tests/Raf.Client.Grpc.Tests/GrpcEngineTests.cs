using System.Text.Json;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Grpc;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Grpc.Tests;

public class GrpcEngineTests
{
    private static VariableDto Var(string key, string value, bool secret = false) => new(Guid.NewGuid(), null, key, value, secret, 1, 0);

    private static GrpcTarget Target(params (string Key, string Value)[] metadata) =>
        new(TestGrpcServer.Address, false, metadata.Select(m => KeyValuePair.Create(m.Key, m.Value)).ToList(), TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Lists_the_methods_by_reflection_with_their_kind()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        var methods = await engine.GetMethodsAsync(Target());

        Assert.Equal(
            [
                ("Chat", GrpcMethodKind.DuplexStreaming), ("Echo", GrpcMethodKind.Unary), ("Fail", GrpcMethodKind.Unary),
                ("Upload", GrpcMethodKind.ClientStreaming), ("Watch", GrpcMethodKind.ServerStreaming), ("Whoami", GrpcMethodKind.Unary),
            ],
            methods.Select(m => (m.Method, m.Kind)));
        Assert.All(methods, m => Assert.Equal("raf.test.Echo", m.Service));
        var echo = methods.Single(m => m.Method == "Echo");
        Assert.Equal(("raf.test.Everything", "raf.test.Everything"), (echo.InputType, echo.OutputType));
        Assert.Contains("\"snakeCaseName\"", echo.InputTemplate);
    }

    [Fact]
    public async Task Calls_a_unary_method_with_json()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        var result = await engine.CallUnaryAsync(new GrpcCall(Target(), "raf.test.Echo", "Echo",
            """{ "s": "hola", "i64": "9007199254740993", "inner": { "values": [1, 2] }, "at": "2024-05-01T00:00:00Z" }"""));

        Assert.True(result.IsOk);
        Assert.Equal("OK", result.StatusName);
        var json = JsonDocument.Parse(result.MessageJson!).RootElement;
        Assert.Equal("hola", json.GetProperty("s").GetString());
        Assert.Equal("9007199254740993", json.GetProperty("i64").GetString());
        Assert.Equal(2, json.GetProperty("inner").GetProperty("values")[1].GetInt32());
        Assert.Equal("2024-05-01T00:00:00Z", json.GetProperty("at").GetString());
    }

    [Fact]
    public async Task Talks_to_a_real_server_without_tls_and_reports_a_closed_port()
    {
        await using var server = await TestGrpcServer.StartAsync(network: true);
        using var engine = new GrpcEngine(); // el handler de la app, por la red
        var target = Target() with { Address = server.NetworkAddress };

        var result = await engine.CallUnaryAsync(new GrpcCall(target, "raf.test.Echo", "Echo", """{ "s": "por la red" }"""));
        var closed = await Assert.ThrowsAsync<GrpcEngineException>(() =>
            engine.GetMethodsAsync(target with { Address = new Uri("http://127.0.0.1:1") }));

        Assert.True(result.IsOk);
        Assert.Contains("por la red", result.MessageJson);
        Assert.Contains("UNAVAILABLE", closed.Message);
    }

    [Fact]
    public async Task Returns_an_error_status_with_its_trailers()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        var result = await engine.CallUnaryAsync(new GrpcCall(Target(), "raf.test.Echo", "Fail", "{}"));

        Assert.Equal((5, "NOT_FOUND", "no está"), (result.StatusCode, result.StatusName, result.StatusDetail));
        Assert.Null(result.MessageJson);
        Assert.Contains(KeyValuePair.Create("x-header", "antes"), result.Headers);
        Assert.Contains(KeyValuePair.Create("x-motivo", "no existe"), result.Trailers);
    }

    [Fact]
    public async Task Sends_the_metadata()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());

        var result = await engine.CallUnaryAsync(new GrpcCall(Target(("authorization", "Bearer abc")), "raf.test.Echo", "Whoami", "{}"));

        Assert.Equal("\"Bearer abc\"", result.MessageJson);
    }

    [Fact]
    public async Task Explains_unknown_methods_and_servers_without_reflection()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var engine = new GrpcEngine(_ => server.CreateHandler());
        await using var bare = await TestGrpcServer.StartAsync(reflection: false);
        using var bareEngine = new GrpcEngine(_ => bare.CreateHandler());

        var unknown = await Assert.ThrowsAsync<GrpcEngineException>(() =>
            engine.CallUnaryAsync(new GrpcCall(Target(), "raf.test.Echo", "Nope", "{}")));
        var noReflection = await Assert.ThrowsAsync<GrpcEngineException>(() => bareEngine.GetMethodsAsync(Target()));

        Assert.Equal(string.Format(CoreTexts.Grpc_UnknownMethod, "raf.test.Echo/Nope"), unknown.Message);
        Assert.Equal(CoreTexts.Grpc_NoReflection, noReflection.Message);
    }

    [Fact]
    public async Task The_runner_sends_a_grpc_request_with_variables_auth_and_extractions()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var runner = new RequestRunner(grpc: new GrpcEngine(_ => server.CreateHandler()));
        var spec = RequestSpec.EmptyGrpc with
        {
            Url = "{{host}}",
            Body = new BodySpec(BodyType.Json, """{ "s": "{{saludo}}" }"""),
            Auth = AuthSpec.Inherit,
            Grpc = new GrpcSpec("raf.test.Echo", "Echo"),
            Extractions = [new ResponseExtraction("guardado", ExtractionSource.Body, "$.s")],
        };
        var variables = VariableContext.From([Var("host", "http://localhost"), Var("saludo", "hola"), Var("token", "secreto", true)], null);

        using var result = await runner.RunAsync(new RunInput(spec, null, variables, new RunOptions(TimeSpan.FromSeconds(10)),
            new AuthSpec(AuthType.Bearer, "{{token}}")));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(new GrpcStatusInfo(0, "OK", null), result.GrpcStatus);
        Assert.Null(result.StatusCode);
        Assert.Equal("hola", JsonDocument.Parse(result.Body!.GetPreviewText()).RootElement.GetProperty("s").GetString());
        Assert.Equal("hola", result.Extracted["guardado"]);
        var call = Assert.Single(result.Events, e => e.Kind == RunEventKind.Request);
        Assert.Contains("/raf.test.Echo/Echo", call.Title);
        Assert.Contains("authorization: Bearer ***", call.Detail);
    }

    [Fact]
    public async Task A_sample_overrides_the_message_and_the_metadata()
    {
        await using var server = await TestGrpcServer.StartAsync();
        using var runner = new RequestRunner(grpc: new GrpcEngine(_ => server.CreateHandler()));
        var echo = RequestSpec.EmptyGrpc with
        {
            Url = "http://localhost",
            Body = new BodySpec(BodyType.Json, """{ "s": "del request" }"""),
            Auth = AuthSpec.None,
            Grpc = new GrpcSpec("raf.test.Echo", "Echo"),
        };
        var whoami = echo with { Body = new BodySpec(BodyType.Json, "{}"), Grpc = new GrpcSpec("raf.test.Echo", "Whoami") };
        var options = new RunOptions(TimeSpan.FromSeconds(10));

        using var message = await runner.RunAsync(new RunInput(echo,
            SampleOverrides.Empty with { Body = new BodySpec(BodyType.Json, """{ "s": "del sample" }""") }, VariableContext.Empty, options));
        using var metadata = await runner.RunAsync(new RunInput(whoami,
            SampleOverrides.Empty with { Headers = [new KeyValueOverride("authorization", "Bearer del-sample")] }, VariableContext.Empty, options));

        Assert.Contains("del sample", message.Body!.GetPreviewText());
        Assert.Equal("\"Bearer del-sample\"", metadata.Body!.GetPreviewText());
    }

    [Fact]
    public async Task The_runner_explains_what_is_missing()
    {
        using var runner = new RequestRunner();
        var variables = VariableContext.Empty;
        var options = new RunOptions(TimeSpan.FromSeconds(5));
        var withoutMethod = RequestSpec.EmptyGrpc with { Url = "http://localhost:1" };

        using var noEngine = await runner.RunAsync(new RunInput(withoutMethod, null, variables, options));
        using var engineRunner = new RequestRunner(grpc: new GrpcEngine());
        using var noMethod = await engineRunner.RunAsync(new RunInput(withoutMethod, null, variables, options));
        using var badAddress = await engineRunner.RunAsync(new RunInput(
            withoutMethod with { Url = "localhost:50051", Grpc = new GrpcSpec("a.B", "C") }, null, variables, options));

        Assert.Equal(CoreTexts.Run_GrpcUnavailable, noEngine.Error);
        Assert.Equal(CoreTexts.Run_GrpcNoMethod, noMethod.Error);
        Assert.Equal(CoreTexts.Run_GrpcAddress, badAddress.Error);
    }

    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com/")]
    [InlineData("grpcs://api.example.com:8443/ignorado", "https://api.example.com:8443/")]
    [InlineData("grpc://localhost:50051", "http://localhost:50051/")]
    [InlineData(" HTTP://localhost:50051 ", "http://localhost:50051/")]
    public void Reads_the_address_with_or_without_tls(string url, string expected) =>
        Assert.Equal(new Uri(expected), RequestRunner.GrpcAddress(url));

    [Fact]
    public void Turns_headers_and_auth_into_metadata()
    {
        var spec = RequestSpec.EmptyGrpc with
        {
            Headers = [new("X-Uno", "1"), new("authorization", "se reemplaza"), new(" ", "sin clave")],
            Auth = new AuthSpec(AuthType.Basic, Username: "ana", Password: "clave"),
        };
        var apiKey = spec with { Auth = new AuthSpec(AuthType.ApiKey, ApiKeyName: "X-Api-Key", ApiKeyValue: "k") };

        Assert.Equal([KeyValuePair.Create("x-uno", "1"), KeyValuePair.Create("authorization", "Basic YW5hOmNsYXZl")],
            RequestRunner.GrpcMetadata(spec));
        Assert.Contains(KeyValuePair.Create("x-api-key", "k"), RequestRunner.GrpcMetadata(apiKey));
    }
}
