using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Mcp;

/// <summary>
/// Herramientas de lectura (Plan 12, etapa 2). Nunca devuelven el valor de una variable secreta (M7): los specs van
/// con los <c>{{nombres}}</c> sin resolver. Los workspaces, Apps y environments se indican por id o por nombre.
/// </summary>
public sealed class RafReadTools(RafMcpContext context)
{
    [McpServerTool(Name = "list_workspaces", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the RAF workspaces of the connected user, with their role (Viewer, Editor or Owner).")]
    public Task<string> ListWorkspacesAsync(CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var store = context.Store(McpAccess.Read);
        return (await store.GetWorkspacesAsync(ct)).Select(w => new { w.Id, w.Name, Role = w.MyRole }).ToList();
    });

    [McpServerTool(Name = "list_environments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the environments of a workspace and which one is active in the RAF app. Requests use the active one unless another is given.")]
    public Task<string> ListEnvironmentsAsync(
        [Description("Workspace id or name.")] string workspace, CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var store = context.Store(McpAccess.Read);
        var ws = await McpLookup.WorkspaceAsync(store, workspace, ct);
        var active = context.Settings.ActiveEnvironments.TryGetValue(ws.Id, out var id) ? id : (Guid?)null;
        return (await store.GetEnvironmentsAsync(ws.Id, ct)).Select(e => new { e.Id, e.Name, Active = e.Id == active }).ToList();
    });

    [McpServerTool(Name = "get_tree", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the tree of a workspace: apps, folders, requests (with kind, method and URL) and their samples. Optionally only one app.")]
    public Task<string> GetTreeAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Optional app id or name, to return only that app.")] string? app = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var store = context.Store(McpAccess.Read);
        var ws = await McpLookup.WorkspaceAsync(store, workspace, ct);
        var apps = await store.GetAppsAsync(ws.Id, ct);
        if (app is not null) apps = [McpLookup.App(apps, app)];
        var folders = await store.GetFoldersAsync(ws.Id, ct);
        var requests = await store.GetRequestsAsync(ws.Id, ct);
        var samples = await store.GetSamplesAsync(ws.Id, ct);

        object Request(RequestDto r) => new
        {
            r.Id, r.Name, Kind = KindOf(r.Spec), r.Spec.Method, r.Spec.Url,
            Samples = samples.Where(s => s.RequestId == r.Id).OrderBy(s => s.Order).Select(s => new { s.Id, s.Name }).ToList(),
        };
        object Folder(FolderDto f) => new
        {
            f.Id, f.Name,
            Folders = folders.Where(x => x.ParentFolderId == f.Id).OrderBy(x => x.Order).Select(Folder).ToList(),
            Requests = requests.Where(r => r.FolderId == f.Id).OrderBy(r => r.Order).Select(Request).ToList(),
        };

        return apps.OrderBy(a => a.Order).Select(a => new
        {
            a.Id, a.Name,
            Folders = folders.Where(f => f.AppId == a.Id && f.ParentFolderId is null).OrderBy(f => f.Order).Select(Folder).ToList(),
            Requests = requests.Where(r => r.AppId == a.Id && r.FolderId is null).OrderBy(r => r.Order).Select(Request).ToList(),
        }).ToList();
    });

    [McpServerTool(Name = "get_request", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns a request with its full spec, or a sample with what it overrides and the resulting spec. Variables appear as {{name}}, unresolved. Give exactly one of request or sample.")]
    public Task<string> GetRequestAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Request id.")] Guid? request = null,
        [Description("Sample id.")] Guid? sample = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        if ((request is null) == (sample is null)) throw new McpException(CoreTexts.Mcp_RequestOrSample);
        var store = context.Store(McpAccess.Read);
        var ws = await McpLookup.WorkspaceAsync(store, workspace, ct);
        if (request is { } requestId)
        {
            var r = await store.GetRequestAsync(ws.Id, requestId, ct);
            return new { r.Id, r.Name, r.AppId, r.FolderId, Kind = KindOf(r.Spec), r.Spec, r.Version };
        }

        var s = await store.GetSampleAsync(ws.Id, sample!.Value, ct);
        var parent = await store.GetRequestAsync(ws.Id, s.RequestId, ct);
        return new
        {
            s.Id, s.Name, s.RequestId, RequestName = parent.Name, parent.AppId, s.Overrides,
            Effective = RequestRunner.Effective(parent.Spec, s.Overrides), s.Version,
        };
    });

    [McpServerTool(Name = "list_variables", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the variables of a workspace with their scope: global or an environment. All apps share them. Secret values are never returned. Environment variables win over globals.")]
    public Task<string> ListVariablesAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("Optional environment id or name: only its variables and the globals.")] string? environment = null,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var store = context.Store(McpAccess.Read);
        var ws = await McpLookup.WorkspaceAsync(store, workspace, ct);
        var environments = await store.GetEnvironmentsAsync(ws.Id, ct);
        var env = environment is null ? null : McpLookup.Environment(environments, environment);

        return (await store.GetVariablesAsync(ws.Id, ct))
            .Where(v => env is null || v.EnvironmentId is null || v.EnvironmentId == env.Id)
            .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(v => new
            {
                v.Key,
                Value = v.IsSecret ? null : v.Value,
                Secret = v.IsSecret,
                Environment = v.EnvironmentId is { } e ? environments.FirstOrDefault(x => x.Id == e)?.Name : null,
            })
            .ToList();
    });

    [McpServerTool(Name = "get_app", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns an app with the authentication its requests inherit and its automatic login, if any.")]
    public Task<string> GetAppAsync(
        [Description("Workspace id or name.")] string workspace,
        [Description("App id or name.")] string app,
        CancellationToken ct = default) => context.RunAsync(async () =>
    {
        var store = context.Store(McpAccess.Read);
        var ws = await McpLookup.WorkspaceAsync(store, workspace, ct);
        var a = McpLookup.App(await store.GetAppsAsync(ws.Id, ct), app);
        var login = a.LoginId is { } id ? (await store.GetLoginsAsync(ws.Id, ct)).FirstOrDefault(l => l.Id == id) : null;
        return new { a.Id, a.Name, a.Auth, Login = login is null ? null : new { login.Id, login.Name, login.Config }, a.Version };
    });

    internal static string KindOf(RequestSpec spec) => spec.IsGrpc ? "grpc" : spec.IsWebSocket ? "webSocket" : "http";
}

/// <summary>Encuentra workspaces, Apps y environments por id o por nombre (sin distinguir mayúsculas).</summary>
internal static class McpLookup
{
    public static async Task<WorkspaceDto> WorkspaceAsync(IWorkspaceStore store, string text, CancellationToken ct) =>
        Find(await store.GetWorkspacesAsync(ct), text, w => w.Id, w => w.Name, CoreTexts.Mcp_KindWorkspace);

    public static AppDto App(IReadOnlyList<AppDto> apps, string text) => Find(apps, text, a => a.Id, a => a.Name, CoreTexts.Mcp_KindApp);

    public static EnvironmentDto Environment(IReadOnlyList<EnvironmentDto> environments, string text) =>
        Find(environments, text, e => e.Id, e => e.Name, CoreTexts.Mcp_KindEnvironment);

    private static T Find<T>(IReadOnlyList<T> items, string text, Func<T, Guid> id, Func<T, string> name, string kind)
    {
        var key = text.Trim();
        if (Guid.TryParse(key, out var guid) && items.FirstOrDefault(i => id(i) == guid) is { } byId) return byId;
        var matches = items.Where(i => string.Equals(name(i), key, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new McpException(string.Format(CoreTexts.Mcp_NotFound, kind, key)),
            _ => throw new McpException(string.Format(CoreTexts.Mcp_Ambiguous, kind, key)),
        };
    }
}
