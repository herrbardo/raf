using Google.Protobuf;
using Raf.Client.Core.Grpc;
using Raf.Client.Grpc.Tests.Proto;
using Raf.Domain.Requests;

namespace Raf.Client.Grpc.Tests;

/// <summary>Los .proto compilados por RAF (Plan 10, etapa 2) tienen que describir lo mismo que el reflection.</summary>
public class ProtoCompilerTests
{
    private static readonly string TestProto = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Protos", "test.proto"));

    [Fact]
    public void Compiles_the_test_contract_with_googles_imports_like_reflection_does()
    {
        var (set, errors) = ProtoCompiler.Compile([new ProtoFile("raf/test.proto", TestProto)]);

        Assert.Empty(errors);
        Assert.Equal(["Chat", "Echo", "Fail", "Upload", "Watch", "Whoami"], set!.Methods.Select(m => m.Method));
        Assert.Equal(GrpcMethodKind.DuplexStreaming, set.Methods.Single(m => m.Method == "Chat").Kind);

        // Con los descriptores compilados, el traductor codifica igual que la clase generada.
        var original = new Everything { S = "compilado", I64 = 7, At = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UnixEpoch) };
        var json = new ProtoJson(set.FindMessage);
        var bytes = json.Encode(JsonFormatter.Default.Format(original), set.FindMessage("raf.test.Everything")!);
        Assert.Equal(original, Everything.Parser.ParseFrom(bytes));
    }

    [Fact]
    public void Resolves_imports_between_files_of_the_set()
    {
        ProtoFile[] files =
        [
            new("acme/users/v1/users.proto", """
                syntax = "proto3";
                package acme.users.v1;
                import "acme/common/v1/page.proto";
                service Users { rpc List(acme.common.v1.Page) returns (acme.common.v1.Page); }
                """),
            new("acme/common/v1/page.proto", """
                syntax = "proto3";
                package acme.common.v1;
                message Page { int32 size = 1; string token = 2; }
                """),
        ];

        var (set, errors) = ProtoCompiler.Compile(files);

        Assert.Empty(errors);
        var method = Assert.Single(set!.Methods);
        Assert.Equal(("acme.users.v1.Users", "List", "acme.common.v1.Page"), (method.Service, method.Method, method.InputType));
    }

    [Fact]
    public async Task Calls_a_server_without_reflection_thanks_to_the_proto_set()
    {
        await using var server = await TestGrpcServer.StartAsync(reflection: false);
        using var runner = new Core.Execution.RequestRunner(grpc: new GrpcEngine(_ => server.CreateHandler()));
        var protos = new GrpcProtoSource(Guid.NewGuid(), 1, [new ProtoFile("raf/test.proto", TestProto)]);
        var spec = RequestSpec.EmptyGrpc with
        {
            Url = "http://localhost",
            Body = new BodySpec(BodyType.Json, """{ "s": "sin reflection" }"""),
            Auth = AuthSpec.None,
            Grpc = new GrpcSpec("raf.test.Echo", "Echo", GrpcDescriptorSource.ProtoSet, protos.Id),
        };
        var options = new Core.Execution.RunOptions(TimeSpan.FromSeconds(10));

        using var result = await runner.RunAsync(new Core.Execution.RunInput(spec, null, Core.Execution.VariableContext.Empty, options, Protos: protos));
        using var missing = await runner.RunAsync(new Core.Execution.RunInput(spec, null, Core.Execution.VariableContext.Empty, options));
        var methods = await runner.Grpc!.GetMethodsAsync(new GrpcTarget(TestGrpcServer.Address, false, [], TimeSpan.FromSeconds(5), protos));

        Assert.True(result.GrpcStatus!.IsOk, result.Error);
        Assert.Contains("sin reflection", result.Body!.GetPreviewText());
        Assert.Equal(CoreTexts.Grpc_ProtoSetMissing, missing.Error);
        Assert.Equal(6, methods.Count);
    }

    [Fact]
    public void Explains_errors_with_file_and_line()
    {
        var (broken, syntax) = ProtoCompiler.Compile([new ProtoFile("roto.proto", "syntax = \"proto3\";\nmessage A {\n  int32 x = ;\n}\n")]);
        var (_, missing) = ProtoCompiler.Compile([new ProtoFile("a.proto", "syntax = \"proto3\";\nimport \"no/existe.proto\";\n")]);
        var (_, empty) = ProtoCompiler.Compile([]);

        Assert.Null(broken);
        Assert.Contains(syntax, e => e.StartsWith("roto.proto:3:", StringComparison.Ordinal));
        Assert.Contains(missing, e => e.Contains("no/existe.proto"));
        Assert.Equal([CoreTexts.Grpc_ProtoEmpty], empty);
    }
}
