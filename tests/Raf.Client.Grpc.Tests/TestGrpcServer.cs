using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Raf.Client.Grpc.Tests.Proto;

namespace Raf.Client.Grpc.Tests;

/// <summary>Un servidor gRPC en memoria con el servicio <c>raf.test.Echo</c> y, si se pide, server reflection.</summary>
public sealed class TestGrpcServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestGrpcServer(WebApplication app) => _app = app;

    /// <summary>
    /// Con <paramref name="network"/> escucha de verdad en un puerto libre de 127.0.0.1, HTTP/2 sin TLS (h2c), y
    /// <see cref="NetworkAddress"/> dice dónde; si no, todo queda en memoria y se llega con <see cref="CreateHandler"/>.
    /// </summary>
    public static async Task<TestGrpcServer> StartAsync(bool reflection = true, bool network = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        if (network)
            builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0,
                l => l.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        else
            builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        if (reflection) builder.Services.AddGrpcReflection();
        var app = builder.Build();
        app.MapGrpcService<EchoService>();
        if (reflection) app.MapGrpcReflectionService();
        await app.StartAsync();
        return new TestGrpcServer(app);
    }

    /// <summary>Para <see cref="GrpcEngine"/>: todas las llamadas van al servidor en memoria.</summary>
    public HttpMessageHandler CreateHandler() => new ResponseVersionHandler(_app.GetTestServer().CreateHandler());

    public static readonly Uri Address = new("http://localhost/");

    public Uri NetworkAddress => new(_app.Urls.First());

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>TestServer responde HTTP/1.1; Grpc.Net.Client exige que la versión coincida con la pedida.</summary>
    private sealed class ResponseVersionHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            response.Version = request.Version;
            return response;
        }
    }

    private sealed class EchoService : Echo.EchoBase
    {
        public override Task<Everything> Echo(Everything request, ServerCallContext context) => Task.FromResult(request);

        public override async Task<Empty> Fail(Empty request, ServerCallContext context)
        {
            await context.WriteResponseHeadersAsync(new Metadata { { "x-header", "antes" } });
            context.ResponseTrailers.Add("x-motivo", "no existe");
            throw new RpcException(new Status(StatusCode.NotFound, "no está"));
        }

        public override Task<StringValue> Whoami(Empty request, ServerCallContext context) =>
            Task.FromResult(new StringValue { Value = context.RequestHeaders.GetValue("authorization") ?? "" });

        /// <summary>Un mensaje por cada valor pedido; con "infinito", sigue hasta que lo cancelen. Termina con un trailer.</summary>
        public override async Task Watch(Inner request, IServerStreamWriter<Inner> responseStream, ServerCallContext context)
        {
            if (request.Name == "infinito")
            {
                for (var i = 0; !context.CancellationToken.IsCancellationRequested; i++)
                {
                    await responseStream.WriteAsync(new Inner { Name = $"vuelta {i}" });
                    await Task.Delay(20, context.CancellationToken);
                }
                return;
            }
            foreach (var value in request.Values)
                await responseStream.WriteAsync(new Inner { Name = $"{request.Name} {value}", Values = { value } });
            context.ResponseTrailers.Add("x-total", request.Values.Count.ToString());
        }

        /// <summary>Junta los nombres y suma los valores de todo lo recibido.</summary>
        public override async Task<Inner> Upload(IAsyncStreamReader<Inner> requestStream, ServerCallContext context)
        {
            var names = new List<string>();
            var sum = 0;
            await foreach (var item in requestStream.ReadAllAsync())
            {
                names.Add(item.Name);
                sum += item.Values.Sum();
            }
            return new Inner { Name = string.Join(",", names), Values = { sum } };
        }

        /// <summary>Contesta cada mensaje; "chau" lo corta con un error.</summary>
        public override async Task Chat(IAsyncStreamReader<Inner> requestStream, IServerStreamWriter<Inner> responseStream, ServerCallContext context)
        {
            await foreach (var item in requestStream.ReadAllAsync())
            {
                if (item.Name == "chau") throw new RpcException(new Status(StatusCode.Aborted, "me fui"));
                await responseStream.WriteAsync(new Inner { Name = $"eco {item.Name}" });
            }
        }
    }
}
