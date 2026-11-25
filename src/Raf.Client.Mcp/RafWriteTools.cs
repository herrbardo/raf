using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Raf.Client.Core.Api;
using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Mcp;

/// <summary>
/// Escritura (Plan 12, etapa 4): crear, modificar y borrar con las validaciones del servidor. Las modificaciones
/// aceptan la versión leída: si otro la cambió, el servidor responde con un conflicto y no se pisa nada. Borrar
/// tiene herramientas propias (M11), para que el harness pueda pedir confirmación solo ahí.
/// </summary>
public sealed class RafWriteTools(RafMcpContext context)
{
    private const string SpecShape =
        """JSON object with any of: method, url, headers and queryParams ([{"key","value","enabled"}]), body ({"type": "None|Json|Text|Xml|FormUrlEncoded", "content", "form"}), auth ({"type": "None|Bearer|Basic|ApiKey|Inherit", "token", "username", "password", "apiKeyName", "apiKeyValue", "apiKeyIn"}) and extractions ([{"variable", "source": "Body|Header", "path"}]). Use {{name}} for variables.""";

    private const string NoneValue = "none";

    // ---------- Environments ----------

    [McpServerTool(Name = "create_environment", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Creates an environment in a workspace.")]
    public Task<string> CreateEnvironmentAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Environment name.")] string name,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        return await store.CreateEnvironmentAsync(ws.Id, name, ct: ct);
    });

    [McpServerTool(Name = "delete_environment", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Deletes an environment and its variables.")]
    public Task<string> DeleteEnvironmentAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Environment id or name.")] string environment,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var env = McpLookup.Environment(await store.GetEnvironmentsAsync(ws.Id, ct), environment);
        await store.DeleteEnvironmentAsync(ws.Id, env.Id, ct);
        return new { Deleted = env.Id };
    });

    // ---------- Variables ----------

    [McpServerTool(Name = "set_variable", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Creates or updates a variable by key, environment and app. A secret value can be written but is never returned.")]
    public Task<string> SetVariableAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Variable key, used as {{key}}.")] string key,
        [Description("Value.")] string value,
        [Description("Optional environment id or name. Without it, a global variable.")] string? environment = null,
        [Description("Optional app id or name. Without it, a variable for all apps.")] string? app = null,
        [Description("Whether the value is secret. Without it, an existing variable keeps its setting and a new one is not secret.")] bool? secret = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var (envId, appId) = await ScopeAsync(store, ws.Id, environment, app, ct);
        var existing = (await store.GetVariablesAsync(ws.Id, ct))
            .FirstOrDefault(v => v.Key == key.Trim() && v.EnvironmentId == envId && v.AppId == appId);
        var saved = existing is null
            ? await store.CreateVariableAsync(ws.Id, new CreateVariableRequest(key, value, envId, secret ?? false, AppId: appId), ct)
            : await store.UpdateVariableAsync(ws.Id, existing.Id,
                new UpdateVariableRequest(key, value, secret ?? existing.IsSecret, existing.Version, appId ?? Guid.Empty), ct);
        return new { saved.Id, saved.Key, Value = saved.IsSecret ? null : saved.Value, Secret = saved.IsSecret, saved.EnvironmentId, saved.AppId };
    });

    [McpServerTool(Name = "delete_variable", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Deletes a variable by key, environment and app.")]
    public Task<string> DeleteVariableAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Variable key.")] string key,
        [Description("Optional environment id or name. Without it, the global variable.")] string? environment = null,
        [Description("Optional app id or name. Without it, the variable for all apps.")] string? app = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var (envId, appId) = await ScopeAsync(store, ws.Id, environment, app, ct);
        var variable = (await store.GetVariablesAsync(ws.Id, ct))
            .FirstOrDefault(v => v.Key == key.Trim() && v.EnvironmentId == envId && v.AppId == appId)
            ?? throw new McpException(string.Format(CoreTexts.Mcp_NotFound, CoreTexts.Mcp_KindVariable, key));
        await store.DeleteVariableAsync(ws.Id, variable.Id, ct);
        return new { Deleted = variable.Id };
    });

    // ---------- Apps ----------

    [McpServerTool(Name = "create_app", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Creates an app at the end of the workspace, optionally with the authentication its requests inherit and a shared automatic login.")]
    public Task<string> CreateAppAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("App name.")] string name,
        [Description("""Optional authentication, for example {"type": "Bearer", "token": "{{token}}"}. It cannot be Inherit.""")] JsonElement? auth = null,
        [Description("Optional shared login id or name.")] string? login = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var apps = await store.GetAppsAsync(ws.Id, ct);
        var loginId = login is null ? (Guid?)null : await LoginIdAsync(store, ws.Id, login, ct);
        return await store.CreateAppAsync(ws.Id, new CreateAppRequest(name, NextOrder(apps.Select(a => a.Order)),
            Auth: auth is null ? null : Parse<AuthSpec>(auth.Value), LoginId: loginId), ct);
    });

    [McpServerTool(Name = "update_app", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Renames an app or changes its authentication or shared login. What is not given does not change.")]
    public Task<string> UpdateAppAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("App id or name.")] string app,
        [Description("Optional new name.")] string? name = null,
        [Description("Optional new authentication (replaces the current one).")] JsonElement? auth = null,
        [Description("Optional shared login id or name, or \"none\" to remove it.")] string? login = null,
        [Description("Optional version read before; if the app changed since, nothing is saved.")] long? version = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var current = McpLookup.App(await store.GetAppsAsync(ws.Id, ct), app);
        Guid? loginId = login is null ? null
            : login.Equals(NoneValue, StringComparison.OrdinalIgnoreCase) ? Guid.Empty
            : await LoginIdAsync(store, ws.Id, login, ct);
        return await store.UpdateAppAsync(ws.Id, current.Id, new UpdateAppRequest(
            name ?? current.Name, current.Order, version ?? current.Version,
            auth is null ? null : Parse<AuthSpec>(auth.Value), loginId), ct);
    });

    [McpServerTool(Name = "delete_app", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Deletes an app with all its folders, requests, samples and variables.")]
    public Task<string> DeleteAppAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("App id or name.")] string app,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var current = McpLookup.App(await store.GetAppsAsync(ws.Id, ct), app);
        await store.DeleteAppAsync(ws.Id, current.Id, ct);
        return new { Deleted = current.Id };
    });

    // ---------- Carpetas ----------

    [McpServerTool(Name = "create_folder", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Creates a folder at the end of an app, or inside another folder.")]
    public Task<string> CreateFolderAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("App id or name.")] string app,
        [Description("Folder name.")] string name,
        [Description("Optional parent folder id.")] Guid? parent = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var target = McpLookup.App(await store.GetAppsAsync(ws.Id, ct), app);
        var siblings = (await store.GetFoldersAsync(ws.Id, ct)).Where(f => f.AppId == target.Id && f.ParentFolderId == parent);
        return await store.CreateFolderAsync(ws.Id,
            new CreateFolderRequest(target.Id, name, parent, NextOrder(siblings.Select(f => f.Order))), ct);
    });

    [McpServerTool(Name = "update_folder", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Renames a folder or moves it inside another folder of the same app. What is not given does not change.")]
    public Task<string> UpdateFolderAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Folder id.")] Guid folder,
        [Description("Optional new name.")] string? name = null,
        [Description("Optional new parent folder id, or \"none\" to move it to the app root.")] string? parent = null,
        [Description("Optional version read before; if the folder changed since, nothing is saved.")] long? version = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var current = (await store.GetFoldersAsync(ws.Id, ct)).FirstOrDefault(f => f.Id == folder)
            ?? throw new McpException(string.Format(CoreTexts.Mcp_NotFound, CoreTexts.Mcp_KindFolder, folder));
        return await store.UpdateFolderAsync(ws.Id, current.Id, new UpdateFolderRequest(
            name ?? current.Name, parent is null ? current.ParentFolderId : ParseOptionalId(parent),
            current.Order, version ?? current.Version), ct);
    });

    [McpServerTool(Name = "delete_folder", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Deletes a folder with its subfolders, requests and samples.")]
    public Task<string> DeleteFolderAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Folder id.")] Guid folder,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        await store.DeleteFolderAsync(ws.Id, folder, ct);
        return new { Deleted = folder };
    });

    // ---------- Requests ----------

    [McpServerTool(Name = "create_request", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Creates an HTTP request at the end of an app or of a folder. " + SpecShape + " What is not given takes the default: GET, no headers, no body, no auth.")]
    public Task<string> CreateRequestAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("App id or name.")] string app,
        [Description("Request name.")] string name,
        [Description("Optional request spec.")] JsonElement? spec = null,
        [Description("Optional folder id inside the app.")] Guid? folder = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var target = McpLookup.App(await store.GetAppsAsync(ws.Id, ct), app);
        var siblings = (await store.GetRequestsAsync(ws.Id, ct)).Where(r => r.AppId == target.Id && r.FolderId == folder);
        return await store.CreateRequestAsync(ws.Id, new CreateRequestRequest(
            target.Id, name, Merge(RequestSpec.Empty, spec), folder, NextOrder(siblings.Select(r => r.Order))), ct);
    });

    [McpServerTool(Name = "update_request", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Updates a request. changes is a partial spec: each property given replaces the current one, the rest stays (for example only {\"url\": \"...\"} or only the body). " + SpecShape)]
    public Task<string> UpdateRequestAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Request id.")] Guid request,
        [Description("Optional partial spec with the properties to replace.")] JsonElement? changes = null,
        [Description("Optional new name.")] string? name = null,
        [Description("Optional folder id to move it to, or \"none\" to move it to the app root.")] string? folder = null,
        [Description("Optional version read before; if the request changed since, nothing is saved.")] long? version = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var current = await store.GetRequestAsync(ws.Id, request, ct);
        return await store.UpdateRequestAsync(ws.Id, current.Id, new UpdateRequestRequest(
            name ?? current.Name, folder is null ? current.FolderId : ParseOptionalId(folder), current.Order,
            Merge(current.Spec, changes), version ?? current.Version), ct);
    });

    [McpServerTool(Name = "delete_request", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Deletes a request with its samples.")]
    public Task<string> DeleteRequestAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Request id.")] Guid request,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        await store.DeleteRequestAsync(ws.Id, request, ct);
        return new { Deleted = request };
    });

    // ---------- Samples ----------

    private const string OverridesShape =
        """overrides is a JSON object with what the sample changes from its request: method, url, body and auth replace the request's; headers and queryParams are [{"key", "value", "enabled", "remove"}] applied over the request's.""";

    [McpServerTool(Name = "create_sample", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Creates a sample of a request, at the end of its samples. " + OverridesShape)]
    public Task<string> CreateSampleAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Request id.")] Guid request,
        [Description("Sample name.")] string name,
        [Description("Optional overrides.")] JsonElement? overrides = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var siblings = (await store.GetSamplesAsync(ws.Id, ct)).Where(s => s.RequestId == request);
        return await store.CreateSampleAsync(ws.Id, new CreateSampleRequest(
            request, name, Merge(SampleOverrides.Empty, overrides), NextOrder(siblings.Select(s => s.Order))), ct);
    });

    [McpServerTool(Name = "update_sample", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Updates a sample. changes replaces the given properties of its overrides and keeps the rest. " + OverridesShape)]
    public Task<string> UpdateSampleAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Sample id.")] Guid sample,
        [Description("Optional partial overrides with the properties to replace.")] JsonElement? changes = null,
        [Description("Optional new name.")] string? name = null,
        [Description("Optional version read before; if the sample changed since, nothing is saved.")] long? version = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        var current = await store.GetSampleAsync(ws.Id, sample, ct);
        return await store.UpdateSampleAsync(ws.Id, current.Id, new UpdateSampleRequest(
            name ?? current.Name, current.Order, Merge(current.Overrides, changes), version ?? current.Version), ct);
    });

    [McpServerTool(Name = "delete_sample", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Deletes a sample.")]
    public Task<string> DeleteSampleAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Sample id.")] Guid sample,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var (store, ws) = await StartAsync(workspace, ct);
        await store.DeleteSampleAsync(ws.Id, sample, ct);
        return new { Deleted = sample };
    });

    // ---------- Ayudas ----------

    private async Task<(IWorkspaceStore Store, WorkspaceDto Workspace)> StartAsync(string workspace, CancellationToken ct)
    {
        var store = context.Store(McpAccess.Write);
        return (store, await McpLookup.WorkspaceAsync(store, workspace, ct));
    }

    private static async Task<(Guid? Environment, Guid? App)> ScopeAsync(
        IWorkspaceStore store, Guid ws, string? environment, string? app, CancellationToken ct) =>
    (
        environment is null ? null : McpLookup.Environment(await store.GetEnvironmentsAsync(ws, ct), environment).Id,
        app is null ? null : McpLookup.App(await store.GetAppsAsync(ws, ct), app).Id
    );

    private static async Task<Guid> LoginIdAsync(IWorkspaceStore store, Guid ws, string login, CancellationToken ct)
    {
        var logins = await store.GetLoginsAsync(ws, ct);
        var key = login.Trim();
        var matches = logins.Where(l => (Guid.TryParse(key, out var id) && l.Id == id) ||
                                        string.Equals(l.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw new McpException(string.Format(CoreTexts.Mcp_NotFound, CoreTexts.Mcp_KindLogin, key)),
            _ => throw new McpException(string.Format(CoreTexts.Mcp_Ambiguous, CoreTexts.Mcp_KindLogin, key)),
        };
    }

    /// <summary>Al final de los hermanos, como cuando se crea desde la app.</summary>
    private static int NextOrder(IEnumerable<int> siblings) => siblings.DefaultIfEmpty(-1).Max() + 1;

    /// <summary>Un id, o "none" para sacar la referencia (por ejemplo, volver a la raíz de la App).</summary>
    private static Guid? ParseOptionalId(string text) =>
        text.Equals(NoneValue, StringComparison.OrdinalIgnoreCase) ? null
        : Guid.TryParse(text, out var id) ? id
        : throw new McpException(string.Format(CoreTexts.Mcp_InvalidId, text));

    private static T Parse<T>(JsonElement json)
    {
        try
        {
            return json.Deserialize<T>(ClientJson.Options) ?? throw new McpException(string.Format(CoreTexts.Mcp_InvalidJson, typeof(T).Name, "null"));
        }
        catch (JsonException e)
        {
            throw new McpException(string.Format(CoreTexts.Mcp_InvalidJson, typeof(T).Name, e.Message));
        }
    }

    /// <summary>
    /// Reemplaza en <paramref name="current"/> cada propiedad que trae <paramref name="changes"/> (sin distinguir
    /// mayúsculas) y deja el resto: así un cambio parcial no borra lo que no se nombró.
    /// </summary>
    internal static T Merge<T>(T current, JsonElement? changes)
    {
        if (changes is not { } json || json.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return current;
        if (json.ValueKind != JsonValueKind.Object)
            throw new McpException(string.Format(CoreTexts.Mcp_InvalidJson, typeof(T).Name, json.ValueKind));

        var node = JsonSerializer.SerializeToNode(current, ClientJson.Options)!.AsObject();
        foreach (var property in json.EnumerateObject())
        {
            var existing = node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) node.Remove(existing);
            node[existing ?? property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        return Parse<T>(JsonSerializer.SerializeToElement(node));
    }
}
