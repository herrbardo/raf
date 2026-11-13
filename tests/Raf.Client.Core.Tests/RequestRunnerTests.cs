using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Localization;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Tests;

/// <summary>Servidor local que devuelve lo que recibió, una línea por dato.</summary>
public sealed class EchoServer : IAsyncLifetime
{
    private WebApplication? _http;
    private WebApplication? _https;

    public string Url { get; private set; } = "";
    public string HttpsUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _http = Build(listen => listen.Listen(IPAddress.Loopback, 0));

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        var password = Guid.NewGuid().ToString("N");
        var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12, password), password);
        _https = Build(listen => listen.Listen(IPAddress.Loopback, 0, o => o.UseHttps(certificate)));

        await _http.StartAsync();
        await _https.StartAsync();
        Url = Address(_http);
        HttpsUrl = Address(_https);
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    /// <summary>Contraseña que acepta /auth/token. Se genera en cada corrida.</summary>
    public string Password { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Cuántos tokens emitió /auth/token. Solo el último es válido en /protected.</summary>
    public int TokensIssued => _issued;

    private int _issued;
    private string? _validToken;

    /// <summary>Un JWT sin firma válida: alcanza para leer el exp.</summary>
    public static string Jwt(DateTimeOffset expires, int n)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("{\"alg\":\"none\"}")}.{Part($"{{\"exp\":{expires.ToUnixTimeSeconds()},\"n\":{n}}}")}.firma";
    }

    private WebApplication Build(Action<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions> listen)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(listen);
        var app = builder.Build();

        app.MapPost("/auth/token", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (!body.Contains($"\"{Password}\"", StringComparison.Ordinal))
                return Results.Json(new { error = "credenciales_rechazadas" }, statusCode: 400);
            var n = Interlocked.Increment(ref _issued);
            var token = Jwt(DateTimeOffset.UtcNow.AddHours(1), n);
            _validToken = token;
            return Results.Json(new { access_token = token, expires_in = 3600 });
        });
        app.MapGet("/protected", (HttpContext http) =>
            _validToken is not null && http.Request.Headers.Authorization == $"Bearer {_validToken}"
                ? Results.Text("ok")
                : Results.StatusCode(401));

        app.MapGet("/slow", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "tarde";
        });
        app.MapGet("/big", () => Results.Text(new string('x', RequestRunner.PreviewLimit + 1000)));
        app.MapPost("/login/{code:int}", (int code, HttpContext http) =>
        {
            http.Response.Headers["X-Session"] = "abc-123";
            return Results.Json(new { access_token = "tok-1", user = new { id = 7 } }, statusCode: code);
        });
        app.Map("/redirect/{code:int}", (int code, HttpContext http) =>
        {
            http.Response.StatusCode = code;
            http.Response.Headers.Location = "/echo";
        });
        app.Map("/echo", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var r = http.Request;
            await http.Response.WriteAsync(string.Join('\n',
                $"method={r.Method}",
                $"query={r.QueryString.Value}",
                $"content-type={r.ContentType}",
                $"authorization={r.Headers.Authorization}",
                $"x-test={r.Headers["X-Test"]}",
                $"x-api-key={r.Headers["X-Api-Key"]}",
                $"body={body}"));
        });
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_http is not null) await _http.DisposeAsync();
        if (_https is not null) await _https.DisposeAsync();
    }
}

public sealed class RequestRunnerTests(EchoServer server) : IClassFixture<EchoServer>, IDisposable
{
    private static readonly RunOptions Defaults = new(TimeSpan.FromSeconds(10));
    private readonly RequestRunner _runner = new();

    public void Dispose() => _runner.Dispose();

    private static RequestSpec Spec(string url, string method = "POST") => RequestSpec.Empty with { Method = method, Url = url };

    private static VariableDto Var(string key, string? value, Guid? env = null, bool secret = false) =>
        new(Guid.NewGuid(), env, key, value, secret, 1, 1);

    private async Task<Dictionary<string, string>> EchoAsync(RequestSpec spec, VariableContext? vars = null, SampleOverrides? sample = null)
    {
        using var result = await _runner.RunAsync(new RunInput(spec, sample, vars ?? VariableContext.Empty, Defaults));
        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        return result.Body!.GetPreviewText().Split('\n').Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    }

    private string Echo => server.Url + "/echo";

    [Theory]
    [InlineData(BodyType.Json, "{\"a\":1}", "application/json")]
    [InlineData(BodyType.Text, "hola", "text/plain")]
    [InlineData(BodyType.Xml, "<a>1</a>", "application/xml")]
    public async Task Envia_body_de_texto_con_su_content_type(BodyType type, string content, string mediaType)
    {
        var echo = await EchoAsync(Spec(Echo) with { Body = new BodySpec(type, content) });
        Assert.Equal(content, echo["body"]);
        Assert.StartsWith(mediaType, echo["content-type"]);
    }

    [Fact]
    public async Task Envia_formulario_sin_las_filas_desactivadas()
    {
        var form = new[] { new KeyValueItem("a b", "1&2"), new KeyValueItem("off", "x", Enabled: false) };
        var echo = await EchoAsync(Spec(Echo) with { Body = new BodySpec(BodyType.FormUrlEncoded, Form: form) });
        Assert.Equal("a+b=1%262", echo["body"]);
        Assert.StartsWith("application/x-www-form-urlencoded", echo["content-type"]);
    }

    [Fact]
    public async Task Sin_body_no_envia_contenido()
    {
        var echo = await EchoAsync(Spec(Echo, "GET"));
        Assert.Equal("GET", echo["method"]);
        Assert.Equal("", echo["body"]);
    }

    [Fact]
    public async Task Content_type_del_usuario_reemplaza_al_del_body()
    {
        var spec = Spec(Echo) with
        {
            Body = new BodySpec(BodyType.Text, "x"),
            Headers = [new KeyValueItem("Content-Type", "application/vnd.raf+json")],
        };
        Assert.Equal("application/vnd.raf+json", (await EchoAsync(spec))["content-type"]);
    }

    [Fact]
    public async Task Auth_bearer()
    {
        var echo = await EchoAsync(Spec(Echo) with { Auth = new AuthSpec(AuthType.Bearer, Token: "abc") });
        Assert.Equal("Bearer abc", echo["authorization"]);
    }

    [Fact]
    public async Task Auth_basic()
    {
        var echo = await EchoAsync(Spec(Echo) with { Auth = new AuthSpec(AuthType.Basic, Username: "ana", Password: "ñ:1") });
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ana:ñ:1")), echo["authorization"]);
    }

    [Fact]
    public async Task Auth_api_key_en_header()
    {
        var echo = await EchoAsync(Spec(Echo) with { Auth = new AuthSpec(AuthType.ApiKey, ApiKeyName: "X-Api-Key", ApiKeyValue: "k1") });
        Assert.Equal("k1", echo["x-api-key"]);
    }

    [Fact]
    public async Task Auth_api_key_en_query()
    {
        var auth = new AuthSpec(AuthType.ApiKey, ApiKeyName: "key", ApiKeyValue: "a b", ApiKeyIn: ApiKeyLocation.Query);
        var echo = await EchoAsync(Spec(Echo) with { Auth = auth });
        Assert.Equal("?key=a%20b", echo["query"]);
    }

    [Fact]
    public async Task Auth_explicita_gana_sobre_el_header_manual()
    {
        var spec = Spec(Echo) with
        {
            Headers = [new KeyValueItem("Authorization", "manual")],
            Auth = new AuthSpec(AuthType.Bearer, Token: "explicito"),
        };
        Assert.Equal("Bearer explicito", (await EchoAsync(spec))["authorization"]);
    }

    [Fact]
    public async Task Query_se_escapa_y_se_suma_a_la_de_la_url()
    {
        var spec = Spec(Echo + "?fija=1") with
        {
            QueryParams = [new KeyValueItem("q", "a&b c"), new KeyValueItem("off", "x", Enabled: false)],
        };
        Assert.Equal("?fija=1&q=a%26b%20c", (await EchoAsync(spec))["query"]);
    }

    [Fact]
    public async Task Resuelve_variables_con_precedencia_del_environment()
    {
        var env = Guid.NewGuid();
        var vars = VariableContext.From(
            [Var("host", "no-usar"), Var("host", server.Url, env), Var("t", "global")], env);
        var spec = Spec("{{host}}/echo") with
        {
            Headers = [new KeyValueItem("X-Test", "{{t}}")],
            Body = new BodySpec(BodyType.Text, "v={{t}}"),
        };

        var echo = await EchoAsync(spec, vars);
        Assert.Equal("global", echo["x-test"]);
        Assert.Equal("v=global", echo["body"]);
    }

    [Fact]
    public async Task Aplica_el_sample_antes_de_enviar()
    {
        var spec = Spec(Echo) with
        {
            Headers = [new KeyValueItem("X-Test", "padre"), new KeyValueItem("X-Api-Key", "quitar")],
            Body = new BodySpec(BodyType.Text, "padre"),
        };
        var sample = new SampleOverrides(
            Method: "PUT",
            Headers: [new KeyValueOverride("X-Test", "hijo"), new KeyValueOverride("X-Api-Key", Remove: true)]);

        var echo = await EchoAsync(spec, sample: sample);
        Assert.Equal("PUT", echo["method"]);
        Assert.Equal("hijo", echo["x-test"]);
        Assert.Equal("", echo["x-api-key"]);
        Assert.Equal("padre", echo["body"]);
    }

    [Fact]
    public async Task Cancelacion_informa_cancelado()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var result = await _runner.RunAsync(
            new RunInput(Spec(server.Url + "/slow", "GET"), null, VariableContext.Empty, Defaults), cts.Token);
        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Timeout_informa_timeout()
    {
        using var result = await _runner.RunAsync(new RunInput(
            Spec(server.Url + "/slow", "GET"), null, VariableContext.Empty, new RunOptions(TimeSpan.FromMilliseconds(100))));
        Assert.Equal(RunOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Url_invalida_falla_sin_revelar_el_secreto()
    {
        var vars = VariableContext.From([Var("s", "valor-secreto", secret: true)], null);
        using var result = await _runner.RunAsync(new RunInput(Spec("ftp://{{s}}", "GET"), null, vars, Defaults));
        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("valor-secreto", result.Error);
    }

    [Fact]
    public async Task Certificado_invalido_se_rechaza_salvo_que_se_acepte()
    {
        var spec = Spec(server.HttpsUrl + "/echo", "GET");

        using var strict = await _runner.RunAsync(new RunInput(spec, null, VariableContext.Empty, Defaults));
        Assert.Equal(RunOutcome.Failed, strict.Outcome);

        using var accepted = await _runner.RunAsync(new RunInput(
            spec, null, VariableContext.Empty, Defaults with { AcceptInvalidCertificates = true }));
        Assert.Equal(200, accepted.StatusCode);
    }

    [Fact]
    public async Task Body_grande_se_trunca_al_mostrar_pero_se_guarda_completo()
    {
        using var result = await _runner.RunAsync(new RunInput(Spec(server.Url + "/big", "GET"), null, VariableContext.Empty, Defaults));
        var body = result.Body!;
        Assert.True(body.IsTruncated);
        Assert.True(body.Preview.Length <= RequestRunner.PreviewLimit);
        Assert.Equal(RequestRunner.PreviewLimit + 1000, body.Length);

        using var saved = new MemoryStream();
        await body.SaveToAsync(saved);
        Assert.Equal(body.Length, saved.Length);
    }

    [Fact]
    public void Preflight_separa_faltantes_de_secretos_ilegibles_e_ignora_lo_que_no_se_envia()
    {
        var vars = VariableContext.From([Var("s", null, secret: true)], null);
        var spec = Spec("{{falta}}/{{s}}") with
        {
            Headers = [new KeyValueItem("X", "{{desactivada}}", Enabled: false)],
            Auth = new AuthSpec(AuthType.None, Token: "{{otra}}"),
        };

        var check = RequestRunner.Check(spec, null, vars);
        Assert.Equal(["falta"], check.Missing);
        Assert.Equal(["s"], check.UnreadableSecrets);
    }

    [Fact]
    public void Url_del_historial_no_incluye_valores_secretos()
    {
        var vars = VariableContext.From([Var("h", "api.local"), Var("s", "valor-secreto", secret: true)], null);
        var spec = Spec("https://{{h}}/x") with { QueryParams = [new KeyValueItem("k", "{{s}}")] };

        var url = RequestRunner.DescribeUrl(spec, null, vars);
        Assert.Contains("api.local", url);
        Assert.DoesNotContain("valor-secreto", url);
    }

    private static string AllText(RunResult result) =>
        string.Join('\n', result.Events.Select(e => e.Title + '\n' + e.Detail));

    [Fact]
    public async Task Consola_muestra_request_conexion_respuesta_body_y_total()
    {
        var spec = Spec(Echo) with
        {
            Headers = [new KeyValueItem("X-Test", "uno")],
            Body = new BodySpec(BodyType.Text, "hola"),
        };
        using var result = await _runner.RunAsync(new RunInput(spec, null, VariableContext.Empty, Defaults));

        var kinds = result.Events.Select(e => e.Kind).ToList();
        Assert.Equal(RunEventKind.Request, kinds[0]);
        Assert.Equal(RunEventKind.Summary, kinds[^1]);
        Assert.Contains(RunEventKind.Response, kinds);
        Assert.Contains(RunEventKind.Body, kinds);
        Assert.True(kinds.Contains(RunEventKind.Connect) || kinds.Contains(RunEventKind.Reused));

        var request = result.Events[0];
        Assert.StartsWith("POST " + server.Url, request.Title);
        Assert.Contains("X-Test: uno", request.Detail);
        Assert.Contains("Content-Length: 4", request.Detail);
        Assert.EndsWith("hola", request.Detail);
        Assert.Contains("Total:", result.Events[^1].Title);
        Assert.Contains("Espera de la respuesta", result.Events[^1].Detail);
    }

    [Fact]
    public async Task Consola_informa_dns_y_conexion_nueva_y_despues_la_reutiliza()
    {
        // Un runner propio, para que la primera vez no haya conexiones abiertas.
        using var runner = new RequestRunner();
        var spec = Spec(Echo.Replace("127.0.0.1", "localhost"), "GET");

        using var first = await runner.RunAsync(new RunInput(spec, null, VariableContext.Empty, Defaults));
        var dns = Assert.Single(first.Events, e => e.Kind == RunEventKind.Dns);
        Assert.StartsWith("DNS: localhost →", dns.Title);
        Assert.Contains(first.Events, e => e.Kind == RunEventKind.Connect);

        using var second = await runner.RunAsync(new RunInput(spec, null, VariableContext.Empty, Defaults));
        Assert.Contains(second.Events, e => e.Kind == RunEventKind.Reused);
        Assert.DoesNotContain(second.Events, e => e.Kind is RunEventKind.Dns or RunEventKind.Connect);
    }

    [Fact]
    public async Task Consola_enmascara_los_secretos()
    {
        var vars = VariableContext.From(
            [Var("s", "clave secreta", secret: true), Var("p", "pass-secreto", secret: true), Var("t", "tok-secreto", secret: true)], null);
        var spec = Spec(Echo) with
        {
            QueryParams = [new KeyValueItem("q", "{{s}}")],
            Headers = [new KeyValueItem("X-Test", "{{t}}")],
            Body = new BodySpec(BodyType.FormUrlEncoded, Form: [new KeyValueItem("f", "{{s}}")]),
            Auth = new AuthSpec(AuthType.Basic, Username: "ana", Password: "{{p}}"),
        };

        using var result = await _runner.RunAsync(new RunInput(spec, null, vars, Defaults));
        Assert.Equal(200, result.StatusCode);

        var text = AllText(result);
        foreach (var secret in new[] { "clave secreta", "clave%20secreta", "clave+secreta", "pass-secreto", "tok-secreto" })
            Assert.DoesNotContain(secret, text);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("ana:pass-secreto"));
        Assert.DoesNotContain(basic, text);
        Assert.Contains("Basic ***", text);
        Assert.Contains("X-Test: ***", text);
    }

    [Fact]
    public async Task Consola_avisa_las_variables_sin_valor()
    {
        var spec = Spec(Echo) with { Headers = [new KeyValueItem("X-Test", "{{falta}}")] };
        using var result = await _runner.RunAsync(new RunInput(spec, null, VariableContext.Empty, Defaults));
        var warning = Assert.Single(result.Events, e => e.Kind == RunEventKind.Variables);
        Assert.Equal(RunEventLevel.Warning, warning.Level);
        Assert.Contains("falta", warning.Detail);
    }

    [Theory]
    [InlineData(302, "GET", "")]
    [InlineData(303, "GET", "")]
    [InlineData(307, "POST", "hola")]
    [InlineData(308, "POST", "hola")]
    public async Task Sigue_redirecciones_como_HttpClient_y_las_muestra(int code, string method, string body)
    {
        var spec = Spec(server.Url + "/redirect/" + code) with
        {
            Body = new BodySpec(BodyType.Text, "hola"),
            Auth = new AuthSpec(AuthType.Bearer, Token: "abc"),
        };
        using var result = await _runner.RunAsync(new RunInput(spec, null, VariableContext.Empty, Defaults));

        Assert.Equal(200, result.StatusCode);
        var echo = result.Body!.GetPreviewText().Split('\n').Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(method, echo["method"]);
        Assert.Equal(body, echo["body"]);
        Assert.Equal("", echo["authorization"]);

        var redirect = Assert.Single(result.Events, e => e.Kind == RunEventKind.Redirect);
        Assert.Equal($"Redirección {code} → {method} {server.Url}/echo", redirect.Title);
        Assert.Contains("Authorization", redirect.Detail);
        Assert.Equal(2, result.Events.Count(e => e.Kind == RunEventKind.Request));
        Assert.Equal(2, result.Events.Count(e => e.Kind == RunEventKind.Response));
    }

    [Fact]
    public async Task Consola_muestra_el_handshake_tls_y_el_certificado()
    {
        var spec = Spec(server.HttpsUrl + "/echo", "GET");
        using var result = await _runner.RunAsync(new RunInput(
            spec, null, VariableContext.Empty, Defaults with { AcceptInvalidCertificates = true }));

        var tls = Assert.Single(result.Events, e => e.Kind == RunEventKind.Tls);
        Assert.StartsWith("Handshake TLS 1.", tls.Title);
        Assert.Contains("Certificado: CN=localhost", tls.Detail);
        Assert.Contains("acepta certificados inválidos", tls.Detail);
    }

    [Fact]
    public async Task Consola_explica_el_error_de_conexion()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var result = await _runner.RunAsync(new RunInput(
            Spec($"http://127.0.0.1:{port}/", "GET"), null, VariableContext.Empty, Defaults));

        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.Request);
        Assert.DoesNotContain(result.Events, e => e.Kind == RunEventKind.Connect);
        var error = Assert.Single(result.Events, e => e.Kind == RunEventKind.Error);
        Assert.Equal(RunEventLevel.Error, error.Level);
        Assert.Contains("SocketException [ConnectionRefused]", error.Detail);
        Assert.Contains("antes de recibir la respuesta", error.Detail);
    }

    [Fact]
    public async Task Consola_marca_el_timeout_y_la_etapa()
    {
        using var result = await _runner.RunAsync(new RunInput(
            Spec(server.Url + "/slow", "GET"), null, VariableContext.Empty, new RunOptions(TimeSpan.FromMilliseconds(300))));
        var error = Assert.Single(result.Events, e => e.Kind == RunEventKind.Error);
        Assert.StartsWith("Timeout:", error.Title);
        Assert.Contains("antes de recibir la respuesta", error.Detail);
        Assert.Equal(RunEventKind.Summary, result.Events[^1].Kind);
    }

    [Theory]
    [InlineData("{\"a\":1}", "application/json", ResponseFormat.Json)]
    [InlineData("<a><b>1</b></a>", "text/xml", ResponseFormat.Xml)]
    [InlineData("{\"a\":", "application/json", ResponseFormat.Plain)]
    [InlineData("hola", "text/plain", ResponseFormat.Plain)]
    public void Formatea_segun_el_tipo(string text, string mediaType, ResponseFormat expected) =>
        Assert.Equal(expected, ResponseFormatter.Format(text, mediaType).Format);

    // ---- Auth heredada de la App ----

    [Fact]
    public async Task Request_que_hereda_usa_la_auth_de_la_App_y_lo_dice_en_la_consola()
    {
        var vars = VariableContext.From([Var("token", "t-1")], null);
        var appAuth = new AuthSpec(AuthType.Bearer, Token: "{{token}}");
        using var result = await _runner.RunAsync(new RunInput(
            Spec(Echo, "GET") with { Auth = AuthSpec.Inherit }, null, vars, Defaults, appAuth));

        Assert.Contains("authorization=Bearer t-1", result.Body!.GetPreviewText());
        var auth = Assert.Single(result.Events, e => e.Kind == RunEventKind.Auth);
        Assert.Contains("Bearer", auth.Title);
    }

    [Fact]
    public async Task Hereda_sin_auth_en_la_App_y_un_sample_puede_heredar_sobre_un_request_propio()
    {
        var echo = await EchoAsync(Spec(Echo) with { Auth = AuthSpec.Inherit });
        Assert.Equal("", echo["authorization"]);

        var appAuth = new AuthSpec(AuthType.Bearer, Token: "app");
        var own = Spec(Echo) with { Auth = new AuthSpec(AuthType.Bearer, Token: "propio") };
        Assert.Equal("Bearer propio", RequestRunner.Effective(own, null, appAuth).Auth is { } a ? $"Bearer {a.Token}" : "");
        var sample = new SampleOverrides(Auth: AuthSpec.Inherit);
        Assert.Equal("app", RequestRunner.Effective(own, sample, appAuth).Auth.Token);
        Assert.True(RequestRunner.InheritsAuth(own, sample));
        Assert.False(RequestRunner.InheritsAuth(own, null));
    }

    // ---- Extracciones ----

    private static RequestSpec Login(string url) => Spec(url) with
    {
        Extractions =
        [
            new ResponseExtraction("token", ExtractionSource.Body, "$.access_token"),
            new ResponseExtraction("user.id", ExtractionSource.Body, "$.user.id"),
            new ResponseExtraction("sesion", ExtractionSource.Header, "x-session"),
            new ResponseExtraction("falta", ExtractionSource.Body, "$.nada"),
            new ResponseExtraction("apagada", ExtractionSource.Body, "$.access_token", Enabled: false),
        ],
    };

    [Fact]
    public async Task Con_2xx_aplica_las_extracciones_sin_mostrar_los_valores()
    {
        using var result = await _runner.RunAsync(new RunInput(Login(server.Url + "/login/200"), null, VariableContext.Empty, Defaults));

        Assert.Equal(new Dictionary<string, string> { ["token"] = "tok-1", ["user.id"] = "7", ["sesion"] = "abc-123" }, result.Extracted);
        var events = result.Events.Where(e => e.Kind == RunEventKind.Extraction).ToList();
        Assert.Equal(4, events.Count);
        Assert.Single(events, e => e.Level == RunEventLevel.Warning && e.Title.Contains("'falta'"));
        Assert.DoesNotContain(events, e => (e.Title + e.Detail).Contains("tok-1"));
    }

    [Fact]
    public async Task Sin_2xx_no_aplica_las_extracciones()
    {
        using var result = await _runner.RunAsync(new RunInput(Login(server.Url + "/login/401"), null, VariableContext.Empty, Defaults));

        Assert.Empty(result.Extracted);
        var skipped = Assert.Single(result.Events, e => e.Kind == RunEventKind.Extraction);
        Assert.Contains("401", skipped.Title);
    }

    [Fact]
    public async Task Las_variables_de_sesion_ganan_y_se_enmascaran_en_la_consola()
    {
        var env = Guid.NewGuid();
        var vars = VariableContext.From(
            [Var("token", "global"), Var("token", "del-env", env)], env, new Dictionary<string, string> { ["token"] = "de-sesion" });
        Assert.True(vars.IsSession("token"));

        using var result = await _runner.RunAsync(new RunInput(
            Spec(Echo, "GET") with { Headers = [new KeyValueItem("X-Test", "{{token}}")] }, null, vars, Defaults));

        Assert.Contains("x-test=de-sesion", result.Body!.GetPreviewText());
        Assert.DoesNotContain("de-sesion", AllText(result));
        Assert.Equal(Echo, RequestRunner.DescribeUrl(Spec(Echo, "GET"), null, vars));
    }

    // ---- Login automático de la App ----

    private static readonly AuthSpec BearerToken = new(AuthType.Bearer, Token: "{{token}}");

    private AppLogin AppLoginConfig(TokenExpiry expiry = TokenExpiry.JwtExp, int? minutes = null) => new(
        true,
        Spec(server.Url + "/auth/token") with { Body = new BodySpec(BodyType.Json, "{\"user\":\"ana\",\"pass\":\"{{clave}}\"}") },
        ExtractionSource.Body, "$.access_token", "token", expiry, ExpiryMinutes: minutes);

    private VariableContext LoginVars(IReadOnlyDictionary<string, string>? session = null, string? password = null) =>
        VariableContext.From([Var("clave", password ?? server.Password, secret: true)], null, session);

    private Task<RunResult> RunProtectedAsync(
        VariableContext vars, AppLogin config, DateTimeOffset? expiresAt = null, RequestRunner? runner = null, AuthSpec? auth = null) =>
        (runner ?? _runner).RunAsync(new RunInput(
            Spec(server.Url + "/protected", "GET") with { Auth = auth ?? AuthSpec.Inherit }, null, vars, Defaults, BearerToken,
            new AppLoginState(config, expiresAt)));

    [Fact]
    public async Task Sin_token_ejecuta_el_login_guarda_el_token_y_sigue()
    {
        using var result = await RunProtectedAsync(LoginVars(), AppLoginConfig());

        Assert.Equal(200, result.StatusCode);
        var token = Assert.IsType<ObtainedToken>(result.Token);
        Assert.Equal("token", token.Variable);
        Assert.Equal(RequestRunner.JwtExpiry(token.Value), token.ExpiresAt);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));

        var auth = result.Events.Where(e => e.Kind == RunEventKind.Auth).Select(e => e.Title).ToList();
        Assert.Equal(CoreTexts.Login_NoToken, auth[0]);
        Assert.Contains(string.Format(CoreTexts.Login_TokenSaved, "token"), auth);
        Assert.Contains(result.Events, e => e.Title.StartsWith(string.Format(CoreTexts.Login_Prefix, "POST")));
        Assert.Contains(result.Events, e => e.Kind == RunEventKind.Request && e.Title.EndsWith("/protected"));
        Assert.DoesNotContain(token.Value, AllText(result));
        Assert.DoesNotContain(server.Password, AllText(result));
    }

    [Fact]
    public async Task Con_token_vigente_no_ejecuta_el_login()
    {
        using var first = await RunProtectedAsync(LoginVars(), AppLoginConfig());
        var token = first.Token!;
        var issued = server.TokensIssued;

        using var second = await RunProtectedAsync(
            LoginVars(new Dictionary<string, string> { ["token"] = token.Value }), AppLoginConfig(), token.ExpiresAt);

        Assert.Equal(200, second.StatusCode);
        Assert.Null(second.Token);
        Assert.Equal(issued, server.TokensIssued);
        Assert.DoesNotContain(second.Events, e => e.Title.StartsWith(string.Format(CoreTexts.Login_Prefix, "")));
    }

    [Fact]
    public async Task Con_token_por_vencer_ejecuta_el_login_antes_de_enviar()
    {
        using var first = await RunProtectedAsync(LoginVars(), AppLoginConfig());
        var issued = server.TokensIssued;

        // Vence dentro del margen: se renueva antes de enviar, sin pasar por un 401.
        using var second = await RunProtectedAsync(
            LoginVars(new Dictionary<string, string> { ["token"] = first.Token!.Value }), AppLoginConfig(),
            DateTimeOffset.UtcNow + RequestRunner.RenewMargin / 2);

        Assert.Equal(200, second.StatusCode);
        Assert.NotNull(second.Token);
        Assert.Equal(issued + 1, server.TokensIssued);
        Assert.Equal(CoreTexts.Login_Expired, second.Events.First(e => e.Kind == RunEventKind.Auth).Title);
        Assert.DoesNotContain(second.Events, e => e.Title == CoreTexts.Login_Renew401);
    }

    [Fact]
    public async Task Ante_un_401_renueva_el_token_una_vez_y_reintenta()
    {
        using var result = await RunProtectedAsync(
            LoginVars(new Dictionary<string, string> { ["token"] = "viejo" }), AppLoginConfig(TokenExpiry.None));

        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Token!.ExpiresAt);
        var titles = result.Events.Select(e => e.Title).ToList();
        Assert.Contains(CoreTexts.Login_Renew401, titles);
        Assert.Contains(CoreTexts.Login_NoExpiry, result.Events.Select(e => e.Detail));
        Assert.Equal(2, result.Events.Count(e => e.Kind == RunEventKind.Request && e.Title.EndsWith("/protected")));
    }

    [Fact]
    public async Task Si_el_login_falla_no_envia_el_request_y_explica_por_que()
    {
        var wrong = Guid.NewGuid().ToString("N");
        using var result = await RunProtectedAsync(LoginVars(password: wrong), AppLoginConfig());

        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Null(result.Token);
        Assert.Contains("400", result.Error);
        Assert.DoesNotContain(result.Events, e => e.Kind == RunEventKind.Request && e.Title.EndsWith("/protected"));
        var error = Assert.Single(result.Events, e => e.Level == RunEventLevel.Error);
        Assert.Contains("credenciales_rechazadas", error.Detail);
        Assert.DoesNotContain(wrong, AllText(result));
    }

    [Fact]
    public async Task Si_el_login_tambien_falla_tras_un_401_muestra_el_401()
    {
        using var result = await RunProtectedAsync(
            LoginVars(new Dictionary<string, string> { ["token"] = "viejo" }, Guid.NewGuid().ToString("N")), AppLoginConfig());

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(401, result.StatusCode);
        Assert.Null(result.Token);
        Assert.Single(result.Events, e => e.Level == RunEventLevel.Error && e.Kind == RunEventKind.Auth);
    }

    [Fact]
    public async Task Un_viewer_sin_acceso_a_las_credenciales_no_puede_obtener_el_token()
    {
        var vars = VariableContext.From([Var("clave", null, secret: true)], null);
        var issued = server.TokensIssued;
        using var result = await RunProtectedAsync(vars, AppLoginConfig());

        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains("clave", result.Error);
        Assert.Equal(issued, server.TokensIssued);
    }

    [Fact]
    public async Task Un_request_con_auth_propia_no_usa_el_login_de_la_App()
    {
        var issued = server.TokensIssued;
        using var result = await RunProtectedAsync(LoginVars(), AppLoginConfig(), auth: new AuthSpec(AuthType.Bearer, Token: "propio"));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal(issued, server.TokensIssued);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task Vencimiento_fijo_se_cuenta_desde_el_reloj_del_runner()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using var runner = new RequestRunner(new FixedTime(now));
        using var result = await RunProtectedAsync(LoginVars(), AppLoginConfig(TokenExpiry.Fixed, 15), runner: runner);

        Assert.Equal(now.AddMinutes(15), result.Token!.ExpiresAt);
    }

    [Theory]
    [InlineData("3600", 3600)]
    [InlineData("90.5", 90.5)]
    public void Vencimiento_en_segundos_es_desde_ahora(string value, double seconds)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddSeconds(seconds), RequestRunner.ParseExpiry(value, now));
    }

    [Fact]
    public void Vencimiento_como_instante_unix_o_fecha()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), RequestRunner.ParseExpiry("1900000000", now));
        Assert.Equal(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero), RequestRunner.ParseExpiry("2031-05-06T07:08:09Z", now));
        Assert.Equal(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero), RequestRunner.ParseExpiry("2031-05-06 07:08:09", now));
        Assert.Null(RequestRunner.ParseExpiry("pronto", now));
        Assert.Null(RequestRunner.ParseExpiry("-5", now));
        Assert.Null(RequestRunner.ParseExpiry(null, now));
    }

    [Fact]
    public void Exp_del_jwt()
    {
        var expires = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(expires, RequestRunner.JwtExpiry(EchoServer.Jwt(expires, 1)));
        Assert.Null(RequestRunner.JwtExpiry("no-es-un-jwt"));
        Assert.Null(RequestRunner.JwtExpiry("a.%%%.c"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
