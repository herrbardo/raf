using System.Text.Json;
using Raf.Client.Core.Local;
using Raf.Client.Core.Localization;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Server.Tests;

/// <summary>Las herramientas de escritura del MCP (Plan 12, etapa 4).</summary>
public sealed partial class McpToolsTests
{
    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task Creates_updates_partially_and_deletes_with_the_server_rules()
    {
        var admin = await app.AdminAsync();
        var name = "MCP write " + Guid.NewGuid().ToString("N")[..8];
        var ws = await admin.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest(name));
        var w = $"/workspaces/{ws.Id}";

        await using var mcp = await Harness.StartAsync(app);
        mcp.Settings.Mcp = new McpPermissions(Read: true, Run: true);
        var forbidden = await mcp.CallAsync("create_environment", new { workspace = name, name = "qa" });
        Assert.True(forbidden.IsError);
        Assert.Contains(string.Format(CoreTexts.Mcp_NotAllowed, CoreTexts.Mcp_PermissionWrite), forbidden.Text);
        mcp.Settings.Mcp = new McpPermissions(Read: true, Write: true);

        // Environment, App con auth, carpeta, request con spec parcial.
        var qa = await mcp.JsonAsync("create_environment", new { workspace = name, name = "qa" });
        var pagos = await mcp.JsonAsync("create_app", new { workspace = name, name = "Pagos", auth = new { type = "Bearer", token = "{{token}}" } });
        Assert.Equal("Bearer", pagos.GetProperty("auth").GetProperty("type").GetString());
        var folder = await mcp.JsonAsync("create_folder", new { workspace = name, app = "Pagos", name = "Cobros" });
        var created = await mcp.JsonAsync("create_request", new
        {
            workspace = name, app = "Pagos", name = "Cobrar", folder = IdOf(folder),
            spec = new { method = "POST", url = "{{baseUrl}}/cobros", body = new { type = "Json", content = "{ \"monto\": 10 }" } },
        });
        var requestId = IdOf(created);

        // Un cambio parcial solo toca lo que nombra; "none" saca la carpeta.
        var updated = await mcp.JsonAsync("update_request", new
        {
            workspace = name, request = requestId, name = "Cobrar v2", folder = "none", changes = new { URL = "{{baseUrl}}/v2/cobros" },
        });
        var saved = await admin.GetOkAsync<RequestDto>($"{w}/requests/{requestId}");
        Assert.Equal(("Cobrar v2", (Guid?)null, "POST", "{{baseUrl}}/v2/cobros", BodyType.Json),
            (saved.Name, saved.FolderId, saved.Spec.Method, saved.Spec.Url, saved.Spec.Body.Type));

        // La versión vieja no pisa lo que cambió después.
        var stale = await mcp.CallAsync("update_request", new
        {
            workspace = name, request = requestId, version = created.GetProperty("version").GetInt64(), changes = new { method = "PUT" },
        });
        Assert.True(stale.IsError);
        Assert.Equal("POST", (await admin.GetOkAsync<RequestDto>($"{w}/requests/{requestId}")).Spec.Method);
        Assert.True(updated.GetProperty("version").GetInt64() > created.GetProperty("version").GetInt64());

        var badJson = await mcp.CallAsync("create_request", new { workspace = name, app = "Pagos", name = "Mal", spec = new { body = 5 } });
        Assert.True(badJson.IsError);

        // Sample: los cambios se suman a lo que ya sobreescribe.
        var sample = await mcp.JsonAsync("create_sample", new
        {
            workspace = name, request = requestId, name = "Grande", overrides = new { url = "{{baseUrl}}/v2/cobros?grande=1" },
        });
        await mcp.JsonAsync("update_sample", new { workspace = name, sample = IdOf(sample), changes = new { method = "PUT" } });
        var savedSample = await admin.GetOkAsync<SampleDto>($"{w}/samples/{IdOf(sample)}");
        Assert.Equal(("PUT", "{{baseUrl}}/v2/cobros?grande=1"), (savedSample.Overrides.Method, savedSample.Overrides.Url));

        // Variables: el valor secreto entra pero no sale; sin "secret" se conserva.
        var secret = await mcp.CallAsync("set_variable", new { workspace = name, key = "clave", value = "uno", environment = "qa", secret = true });
        Assert.False(secret.IsError, secret.Text);
        Assert.DoesNotContain("uno", secret.Text);
        var again = await mcp.CallAsync("set_variable", new { workspace = name, key = "clave", value = "dos", environment = "qa" });
        Assert.DoesNotContain("dos", again.Text);
        var variable = Assert.Single(await admin.GetOkAsync<List<VariableDto>>($"{w}/variables"));
        Assert.Equal(("dos", true, IdOf(qa)), (variable.Value, variable.IsSecret, variable.EnvironmentId!.Value));

        await mcp.JsonAsync("update_app", new { workspace = name, app = "Pagos", name = "Pagos 2" });
        Assert.Equal("Bearer", (await admin.GetOkAsync<List<AppDto>>($"{w}/apps")).Single().Auth!.Type.ToString());

        // Borrar, cada cosa con su herramienta.
        await mcp.JsonAsync("delete_sample", new { workspace = name, sample = IdOf(sample) });
        await mcp.JsonAsync("delete_variable", new { workspace = name, key = "clave", environment = "qa" });
        await mcp.JsonAsync("delete_request", new { workspace = name, request = requestId });
        await mcp.JsonAsync("delete_folder", new { workspace = name, folder = IdOf(folder) });
        await mcp.JsonAsync("delete_app", new { workspace = name, app = "Pagos 2" });
        await mcp.JsonAsync("delete_environment", new { workspace = name, environment = "qa" });
        Assert.Empty(await admin.GetOkAsync<List<AppDto>>($"{w}/apps"));
        Assert.Empty(await admin.GetOkAsync<List<RequestDto>>($"{w}/requests"));
        Assert.Empty(await admin.GetOkAsync<List<VariableDto>>($"{w}/variables"));
        Assert.Empty(await admin.GetOkAsync<List<EnvironmentDto>>($"{w}/environments"));
        var missing = await mcp.CallAsync("delete_variable", new { workspace = name, key = "clave" });
        Assert.True(missing.IsError);
    }
}
