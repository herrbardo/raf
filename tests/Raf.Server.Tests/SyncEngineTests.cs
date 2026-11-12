using System.Text;
using Raf.Client.Core.Diagnostics;
using Raf.Client.Core.Local;
using Raf.Client.Core.Session;
using Raf.Client.Core.Api;
using Raf.Client.Core.Sync;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Server.Tests;

/// <summary>Corta la conexión a pedido: antes de que el pedido llegue o después, perdiendo solo la respuesta.</summary>
public sealed class FaultHandler : DelegatingHandler
{
    public Func<HttpRequestMessage, bool>? DropBefore { get; set; }
    public Func<HttpRequestMessage, bool>? DropResponse { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (DropBefore?.Invoke(request) == true) throw new HttpRequestException("sin conexión");
        var response = await base.SendAsync(request, ct);
        if (DropResponse?.Invoke(request) == true) throw new HttpRequestException("se perdió la respuesta");
        return response;
    }
}

internal sealed class NoSubscriber : IChangeSubscriber
{
    public event Action? ChangeAvailable { add { } remove { } }
    public void Start() { }
    public void Dispose() { }
}

/// <summary>Un cliente real (caché, bandeja y motor) de un usuario, con su propia carpeta local.</summary>
internal sealed class Device : IAsyncDisposable
{
    private readonly string _dir;

    private Device(string dir, UserDto user, HttpClient raw, FaultHandler fault, SyncSession sync, EventLog events)
    {
        _dir = dir;
        Events = events;
        User = user;
        Raw = raw;
        Fault = fault;
        Sync = sync;
    }

    public UserDto User { get; }
    public HttpClient Raw { get; }
    public FaultHandler Fault { get; }
    public SyncSession Sync { get; }
    public SyncedWorkspaceStore Store => Sync.Store;
    public EventLog Events { get; }

    public List<WorkspaceLost> Lost { get; } = [];

    public static async Task<Device> NewAsync(RafApp app)
    {
        var (user, raw) = await app.CreateActiveUserAsync();
        var dir = Path.Combine(Path.GetTempPath(), "raf-sync-" + Guid.NewGuid().ToString("N"));
        var fault = new FaultHandler();
        var http = app.ClientWith(fault);
        var server = new Uri("http://localhost/");
        var events = new EventLog();
        var session = new SessionManager(http, new MemorySecureStore(), TimeProvider.System, Path.Combine(dir, "session.lock"), events);
        session.SetServer(server);
        await session.LoginAsync(user.Email, "NuevaClave123");

        var sync = await SyncSession.OpenAsync(
            dir, server, user.Id, new RafApiClient(http, session), new MemorySecureStore(), new NoSubscriber(), events);
        var device = new Device(dir, user, raw, fault, sync, events);
        sync.Engine.WorkspaceLost += device.Lost.Add;
        return device;
    }

    public async Task SyncAsync() => Assert.True(await Sync.Engine.SyncOnceAsync(), "El ciclo no pudo terminar.");

    public async Task<SyncState> StateAsync() => (await Sync.Engine.GetStatusAsync()).State;

    public void GoOffline() => Fault.DropBefore = _ => true;

    public void GoOnline() => Fault.DropBefore = null;

    public byte[] CacheBytes() => Directory.GetFiles(_dir, "*.db*")
        .Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
        .SelectMany(f =>
        {
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }).ToArray();

    public async ValueTask DisposeAsync()
    {
        await Sync.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Es una carpeta temporal.
        }
    }
}

public class SyncEngineTests(RafApp app)
{
    /// <summary>Dos clientes en el mismo workspace, ya sincronizados.</summary>
    private async Task<(Device A, Device B, Guid Ws)> TwoDevicesAsync(WorkspaceRole role = WorkspaceRole.Editor)
    {
        var a = await Device.NewAsync(app);
        var b = await Device.NewAsync(app);
        var ws = (await a.Store.CreateWorkspaceAsync("Equipo")).Id;
        await a.SyncAsync();
        await ShareAsync(a, b, ws, role);
        await b.SyncAsync();
        return (a, b, ws);
    }

    private static Task ShareAsync(Device owner, Device with, Guid ws, WorkspaceRole role) =>
        owner.Raw.PostOkAsync<MemberDto>($"/workspaces/{ws}/members", new AddMemberRequest(with.User.Id, role));

    private static async Task<AppDto> OneAppAsync(Device d, Guid ws) => Assert.Single(await d.Store.GetAppsAsync(ws));

    private static async Task<SyncConflict> OneConflictAsync(Device d) => Assert.Single(await d.Store.GetConflictsAsync());

    // ---------- Flujo normal ----------

    [Fact]
    public async Task Un_cambio_llega_al_otro_cliente_y_se_borra_en_ambos()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        var created = await a.Store.CreateAppAsync(ws, new CreateAppRequest("Pagos"));
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("Pagos", (await OneAppAsync(b, ws)).Name);

        var seen = await OneAppAsync(b, ws);
        await b.Store.UpdateAppAsync(ws, seen.Id, new UpdateAppRequest("Cobros", 0, seen.Version));
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal("Cobros", (await OneAppAsync(a, ws)).Name);

        await a.Store.DeleteAppAsync(ws, created.Id);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Empty(await b.Store.GetAppsAsync(ws));
        Assert.Equal(SyncState.Synced, await a.StateAsync());
    }

    [Fact]
    public async Task Sin_conexion_se_trabaja_y_al_volver_se_envia_padre_antes_que_hijo()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        a.GoOffline();
        var app1 = await a.Store.CreateAppAsync(ws, new CreateAppRequest("App"));
        var folder = await a.Store.CreateFolderAsync(ws, new CreateFolderRequest(app1.Id, "Carpeta"));
        var req = await a.Store.CreateRequestAsync(ws, new CreateRequestRequest(app1.Id, "R", FolderId: folder.Id));
        await a.Store.UpdateAppAsync(ws, app1.Id, new UpdateAppRequest("App renombrada", 0, app1.Version));

        Assert.False(await a.Sync.Engine.SyncOnceAsync());
        Assert.Equal(SyncState.Offline, await a.StateAsync());
        Assert.Equal("R", (await a.Store.GetRequestAsync(ws, req.Id)).Name);

        a.GoOnline();
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("App renombrada", (await OneAppAsync(b, ws)).Name);
        Assert.Equal(folder.Id, Assert.Single(await b.Store.GetRequestsAsync(ws)).FolderId);
    }

    [Fact]
    public async Task Sin_conexion_el_estado_dice_por_que_y_al_volver_se_limpia()
    {
        var (a, _, _) = await TwoDevicesAsync();
        var before = DateTimeOffset.Now;
        await a.SyncAsync();
        var synced = await a.Sync.Engine.GetStatusAsync();
        Assert.Null(synced.LastError);
        Assert.True(synced.LastSyncedAt >= before);

        a.GoOffline();
        Assert.False(await a.Sync.Engine.SyncOnceAsync());
        var offline = await a.Sync.Engine.GetStatusAsync();
        Assert.Equal(SyncState.Offline, offline.State);
        Assert.Equal("No se pudo conectar con el servidor. (sin conexión)", offline.LastError);
        Assert.Equal(synced.LastSyncedAt, offline.LastSyncedAt); // conserva la última que salió bien

        a.GoOnline();
        await a.SyncAsync();
        Assert.Null((await a.Sync.Engine.GetStatusAsync()).LastError);
    }

    [Fact]
    public async Task La_consola_anota_el_corte_una_vez_la_recuperacion_y_lo_enviado()
    {
        var (a, _, ws) = await TwoDevicesAsync();
        a.Events.Clear();

        a.GoOffline();
        await a.Store.CreateAppAsync(ws, new CreateAppRequest("Sin red"));
        Assert.False(await a.Sync.Engine.SyncOnceAsync());
        Assert.False(await a.Sync.Engine.SyncOnceAsync()); // el mismo motivo no se repite en cada reintento

        var cut = Assert.Single(a.Events.Snapshot(), e => e.Level == EventLevel.Warning);
        Assert.Equal(EventCategory.Sync, cut.Category);
        Assert.Contains("No se pudo conectar con el servidor. (sin conexión)", cut.Message);

        a.GoOnline();
        await a.SyncAsync();
        var info = a.Events.Snapshot().Where(e => e.Level == EventLevel.Info).Select(e => e.Message).ToList();
        Assert.Contains("Se recuperó la conexión con el servidor después de 2 intentos fallidos.", info);
        Assert.Contains("Se envió 1 cambio al servidor.", info);
    }

    [Fact]
    public async Task La_consola_no_guarda_tokens_ni_valores_de_variables()
    {
        var (a, _, ws) = await TwoDevicesAsync();
        await a.Store.CreateVariableAsync(ws, new CreateVariableRequest("clave", "valor-muy-secreto", null, true));
        await a.SyncAsync();

        var all = string.Join("\n", a.Events.Snapshot().Select(e => e.Message + e.Detail));
        Assert.Contains("Sesión iniciada", all);
        Assert.DoesNotContain("valor-muy-secreto", all);
        Assert.DoesNotContain("eyJ", all);
        Assert.DoesNotContain("NuevaClave123", all);
    }

    [Fact]
    public async Task Crear_y_borrar_sin_conexion_no_llega_al_servidor()
    {
        var (a, _, ws) = await TwoDevicesAsync();
        a.GoOffline();
        var created = await a.Store.CreateAppAsync(ws, new CreateAppRequest("Efimera"));
        await a.Store.DeleteAppAsync(ws, created.Id);
        Assert.Equal(0, (await a.Sync.Engine.GetStatusAsync()).Pending);
    }

    [Fact]
    public async Task Workspace_creado_sin_conexion_se_sube_con_su_contenido()
    {
        var a = await Device.NewAsync(app);
        a.GoOffline();
        var ws = (await a.Store.CreateWorkspaceAsync("Offline")).Id;
        await a.Store.CreateAppAsync(ws, new CreateAppRequest("A"));

        a.GoOnline();
        await a.SyncAsync();
        Assert.Single(await a.Raw.GetOkAsync<List<AppDto>>($"/workspaces/{ws}/apps"));
    }

    // ---------- Conflictos ----------

    private static async Task<(AppDto Seen, SyncConflict Conflict)> VersionConflictAsync(Device a, Device b, Guid ws)
    {
        await a.Store.CreateAppAsync(ws, new CreateAppRequest("Base"));
        await a.SyncAsync();
        await b.SyncAsync();

        var seen = await OneAppAsync(a, ws);
        var other = await OneAppAsync(b, ws);
        await a.Store.UpdateAppAsync(ws, seen.Id, new UpdateAppRequest("De A", 0, seen.Version));
        await b.Store.UpdateAppAsync(ws, other.Id, new UpdateAppRequest("De B", 0, other.Version));
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal(SyncState.Conflict, await a.StateAsync());
        return (seen, await OneConflictAsync(a));
    }

    [Fact]
    public async Task Conflicto_muestra_ambas_versiones_y_gana_la_local_si_se_elige()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        var (_, conflict) = await VersionConflictAsync(a, b, ws);
        Assert.Equal(ConflictReason.Version, conflict.Reason);
        Assert.Contains("De A", conflict.LocalJson);
        Assert.Contains("De B", conflict.RemoteJson);

        await a.Store.UseLocalAsync(conflict.Id);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("De A", (await OneAppAsync(b, ws)).Name);
        Assert.Equal(SyncState.Synced, await a.StateAsync());
    }

    [Fact]
    public async Task Conflicto_resuelto_con_la_version_del_servidor()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        var (_, conflict) = await VersionConflictAsync(a, b, ws);

        await a.Store.DiscardAsync(conflict.Id);
        await a.SyncAsync();
        Assert.Equal("De B", (await OneAppAsync(a, ws)).Name);
    }

    [Fact]
    public async Task Conflicto_resuelto_editando()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        var (_, conflict) = await VersionConflictAsync(a, b, ws);

        await a.Store.EditAsync(conflict.Id, conflict.LocalJson.Replace("De A", "Mezcla"));
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("Mezcla", (await OneAppAsync(b, ws)).Name);
    }

    [Fact]
    public async Task Un_conflicto_no_frena_cambios_independientes()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        await VersionConflictAsync(a, b, ws);

        await a.Store.CreateEnvironmentAsync(ws, "dev");
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Single(await b.Store.GetEnvironmentsAsync(ws));
    }

    [Fact]
    public async Task Una_pestana_con_version_ya_superada_por_cambios_propios_no_es_conflicto()
    {
        var (a, _, ws) = await TwoDevicesAsync();
        await a.Store.CreateAppAsync(ws, new CreateAppRequest("Base"));
        await a.SyncAsync();
        var openInTab = await OneAppAsync(a, ws);

        await a.Store.UpdateAppAsync(ws, openInTab.Id, new UpdateAppRequest("Renombrada", 0, openInTab.Version));
        await a.SyncAsync();

        await a.Store.UpdateAppAsync(ws, openInTab.Id, new UpdateAppRequest("Desde la pestaña", 0, openInTab.Version));
        await a.SyncAsync();
        Assert.Equal(SyncState.Synced, await a.StateAsync());
        Assert.Equal("Desde la pestaña", (await a.Raw.GetOkAsync<AppDto>($"/workspaces/{ws}/apps/{openInTab.Id}")).Name);
    }

    [Fact]
    public async Task Una_pestana_con_version_ajena_superada_si_es_conflicto()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        await a.Store.CreateAppAsync(ws, new CreateAppRequest("Base"));
        await a.SyncAsync();
        await b.SyncAsync();
        var openInTab = await OneAppAsync(a, ws);

        var other = await OneAppAsync(b, ws);
        await b.Store.UpdateAppAsync(ws, other.Id, new UpdateAppRequest("De B", 0, other.Version));
        await b.SyncAsync();
        await a.SyncAsync(); // la caché de A ya tiene la versión de B; la pestaña sigue en la anterior

        await a.Store.UpdateAppAsync(ws, openInTab.Id, new UpdateAppRequest("De la pestaña", 0, openInTab.Version));
        await a.SyncAsync();
        Assert.Equal(ConflictReason.Version, (await OneConflictAsync(a)).Reason);
    }

    // ---------- Fallos de red ----------

    [Fact]
    public async Task Si_se_pierde_la_respuesta_de_un_alta_el_reintento_no_duplica()
    {
        var (a, _, ws) = await TwoDevicesAsync();
        await a.Store.CreateAppAsync(ws, new CreateAppRequest("Una"));

        a.Fault.DropResponse = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/apps", StringComparison.Ordinal);
        Assert.False(await a.Sync.Engine.SyncOnceAsync());
        a.Fault.DropResponse = null;

        await a.SyncAsync();
        Assert.Single(await a.Raw.GetOkAsync<List<AppDto>>($"/workspaces/{ws}/apps"));
        Assert.Equal(SyncState.Synced, await a.StateAsync());
    }

    [Fact]
    public async Task Si_se_pierde_la_respuesta_de_una_edicion_no_hay_conflicto_con_uno_mismo()
    {
        var (a, _, ws) = await TwoDevicesAsync();
        await a.Store.CreateAppAsync(ws, new CreateAppRequest("Una"));
        await a.SyncAsync();
        var seen = await OneAppAsync(a, ws);
        await a.Store.UpdateAppAsync(ws, seen.Id, new UpdateAppRequest("Dos", 0, seen.Version));

        a.Fault.DropResponse = r => r.Method == HttpMethod.Put;
        Assert.False(await a.Sync.Engine.SyncOnceAsync());
        a.Fault.DropResponse = null;

        await a.SyncAsync();
        Assert.Equal(SyncState.Synced, await a.StateAsync());
        Assert.Equal(seen.Version + 1, (await OneAppAsync(a, ws)).Version);
    }

    [Fact]
    public async Task Un_corte_a_mitad_del_envio_continua_donde_quedo()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        for (var i = 0; i < 4; i++) await a.Store.CreateAppAsync(ws, new CreateAppRequest($"App{i}", i));

        var sent = 0;
        a.Fault.DropBefore = r => r.Method == HttpMethod.Post && Interlocked.Increment(ref sent) > 2;
        Assert.False(await a.Sync.Engine.SyncOnceAsync());
        Assert.Equal(2, (await a.Raw.GetOkAsync<List<AppDto>>($"/workspaces/{ws}/apps")).Count);

        a.Fault.DropBefore = null;
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal(4, (await b.Store.GetAppsAsync(ws)).Count);
    }

    // ---------- Casos borde del plan ----------

    private static async Task<RequestDto> ChildOfDeletedAppAsync(Device a, Device b, Guid ws)
    {
        var parent = await a.Store.CreateAppAsync(ws, new CreateAppRequest("Padre"));
        await a.SyncAsync();
        await b.SyncAsync();

        b.GoOffline();
        var child = await b.Store.CreateRequestAsync(ws, new CreateRequestRequest(parent.Id, "Hijo"));

        await a.Store.DeleteAppAsync(ws, parent.Id);
        await a.SyncAsync();

        b.GoOnline();
        await b.SyncAsync();
        return child;
    }

    [Fact]
    public async Task Hijo_creado_sin_conexion_con_padre_borrado_se_puede_recrear()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        await ChildOfDeletedAppAsync(a, b, ws);

        var conflict = await OneConflictAsync(b);
        Assert.Equal(ConflictReason.Missing, conflict.Reason);
        Assert.True(conflict.CanRecreate);

        await b.Store.RecreateAsync(conflict.Id);
        await b.SyncAsync();
        await a.SyncAsync();
        var recreated = await OneAppAsync(a, ws);
        Assert.Equal("Padre", recreated.Name);
        Assert.Equal(recreated.Id, Assert.Single(await a.Store.GetRequestsAsync(ws)).AppId);
        Assert.Equal(SyncState.Synced, await b.StateAsync());
    }

    [Fact]
    public async Task Hijo_creado_sin_conexion_con_padre_borrado_se_puede_descartar()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        await ChildOfDeletedAppAsync(a, b, ws);

        await b.Store.DiscardAsync((await OneConflictAsync(b)).Id);
        await b.SyncAsync();
        Assert.Empty(await b.Store.GetRequestsAsync(ws));
        Assert.Equal(SyncState.Synced, await b.StateAsync());
    }

    [Fact]
    public async Task Editar_un_elemento_que_otro_borro_se_puede_recrear()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        var created = await a.Store.CreateAppAsync(ws, new CreateAppRequest("Una"));
        await a.SyncAsync();
        await b.SyncAsync();

        b.GoOffline();
        var seen = await OneAppAsync(b, ws);
        await b.Store.UpdateAppAsync(ws, seen.Id, new UpdateAppRequest("Editada", 0, seen.Version));
        await a.Store.DeleteAppAsync(ws, created.Id);
        await a.SyncAsync();
        b.GoOnline();
        await b.SyncAsync();

        var conflict = await OneConflictAsync(b);
        Assert.Equal(ConflictReason.Missing, conflict.Reason);
        await b.Store.RecreateAsync(conflict.Id);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal("Editada", (await OneAppAsync(a, ws)).Name);
    }

    [Fact]
    public async Task Variable_duplicada_offline_es_conflicto_y_se_corrige_editando()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        b.GoOffline();
        await b.Store.CreateVariableAsync(ws, new CreateVariableRequest("token", "de-b"));
        await a.Store.CreateVariableAsync(ws, new CreateVariableRequest("token", "de-a"));
        await a.SyncAsync();
        b.GoOnline();
        await b.SyncAsync();

        var conflict = await OneConflictAsync(b);
        Assert.Equal(ConflictReason.Duplicate, conflict.Reason);

        await b.Store.EditAsync(conflict.Id, conflict.LocalJson.Replace("\"token\"", "\"token2\""));
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal(["token", "token2"], (await a.Store.GetVariablesAsync(ws)).Select(v => v.Key));
    }

    [Fact]
    public async Task Al_perder_el_acceso_sin_pendientes_se_borra_la_cache()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        await a.Raw.DeleteAsync($"/workspaces/{ws}/members/{b.User.Id}");

        await b.SyncAsync();
        Assert.Empty(await b.Store.GetWorkspacesAsync());
        Assert.Equal(0, Assert.Single(b.Lost).PendingChanges);
    }

    [Fact]
    public async Task Al_perder_el_acceso_con_pendientes_se_avisa_antes_de_borrar()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        b.GoOffline();
        await b.Store.CreateAppAsync(ws, new CreateAppRequest("Sin enviar"));
        await a.Raw.DeleteAsync($"/workspaces/{ws}/members/{b.User.Id}");
        b.GoOnline();

        await b.SyncAsync();
        Assert.Equal(1, Assert.Single(b.Lost).PendingChanges);
        Assert.Single(await b.Store.GetWorkspacesAsync()); // sigue, a la espera de que el usuario lo vea

        await b.SyncAsync();
        Assert.Single(b.Lost); // no se repite el aviso

        await b.Store.ForgetWorkspaceAsync(ws);
        Assert.Empty(await b.Store.GetWorkspacesAsync());
        Assert.Equal(0, (await b.Sync.Engine.GetStatusAsync()).Pending);
    }

    [Fact]
    public async Task Al_eliminarse_el_workspace_se_borra_la_cache()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        await a.Raw.DeleteAsync($"/workspaces/{ws}");

        await b.SyncAsync();
        Assert.Empty(await b.Store.GetWorkspacesAsync());
        Assert.Single(b.Lost);
    }

    [Fact]
    public async Task De_Viewer_a_Editor_llegan_los_secretos_aunque_no_hayan_cambiado()
    {
        var (a, b, ws) = await TwoDevicesAsync(WorkspaceRole.Viewer);
        await a.Store.CreateVariableAsync(ws, new CreateVariableRequest("token", "s3cr3t", IsSecret: true));
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Null(Assert.Single(await b.Store.GetVariablesAsync(ws)).Value);

        await a.Raw.PutJsonAsync($"/workspaces/{ws}/members/{b.User.Id}", new UpdateMemberRequest(WorkspaceRole.Editor));
        await b.SyncAsync();
        Assert.Equal("s3cr3t", Assert.Single(await b.Store.GetVariablesAsync(ws)).Value);
        Assert.Equal(WorkspaceRole.Editor, Assert.Single(await b.Store.GetWorkspacesAsync()).MyRole);
    }

    [Fact]
    public async Task De_Editor_a_Viewer_se_borran_los_secretos_de_la_cache()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        await a.Store.CreateVariableAsync(ws, new CreateVariableRequest("token", "s3cr3t", IsSecret: true));
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("s3cr3t", Assert.Single(await b.Store.GetVariablesAsync(ws)).Value);

        await a.Raw.PutJsonAsync($"/workspaces/{ws}/members/{b.User.Id}", new UpdateMemberRequest(WorkspaceRole.Viewer));
        await b.SyncAsync();
        Assert.Null(Assert.Single(await b.Store.GetVariablesAsync(ws)).Value);
        await Assert.ThrowsAsync<ApiException>(() => b.Store.CreateAppAsync(ws, new CreateAppRequest("No")));
    }

    [Fact]
    public async Task Cambio_de_isSecret_llega_por_el_feed()
    {
        var (a, b, ws) = await TwoDevicesAsync(WorkspaceRole.Viewer);
        var v = await a.Store.CreateVariableAsync(ws, new CreateVariableRequest("token", "visible"));
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("visible", Assert.Single(await b.Store.GetVariablesAsync(ws)).Value);

        var seen = Assert.Single(await a.Store.GetVariablesAsync(ws));
        await a.Store.UpdateVariableAsync(ws, v.Id, new UpdateVariableRequest("token", "visible", true, seen.Version));
        await a.SyncAsync();
        await b.SyncAsync();
        var masked = Assert.Single(await b.Store.GetVariablesAsync(ws));
        Assert.True(masked.IsSecret);
        Assert.Null(masked.Value);
    }

    [Fact]
    public async Task Un_secreto_nunca_queda_en_claro_en_el_archivo_de_la_cache()
    {
        var (a, _, ws) = await TwoDevicesAsync();
        const string value = "valor-secreto-que-no-debe-verse";
        await a.Store.CreateVariableAsync(ws, new CreateVariableRequest("token", value, IsSecret: true));
        await a.SyncAsync();

        var needle = Encoding.UTF8.GetBytes(value);
        Assert.Equal(-1, a.CacheBytes().AsSpan().IndexOf(needle));
        Assert.Equal(value, Assert.Single(await a.Store.GetVariablesAsync(ws)).Value);
    }

    [Fact]
    public async Task Paginas_grandes_se_traen_completas()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        for (var i = 0; i < 30; i++) await a.Store.CreateAppAsync(ws, new CreateAppRequest($"App{i}", i));
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal(30, (await b.Store.GetAppsAsync(ws)).Count);
    }

    [Fact]
    public async Task Un_conflicto_bloquea_a_los_hijos_del_elemento()
    {
        var (a, b, ws) = await TwoDevicesAsync();
        var (seen, _) = await VersionConflictAsync(a, b, ws);

        await a.Store.CreateRequestAsync(ws, new CreateRequestRequest(seen.Id, "Hijo"));
        await a.SyncAsync();
        Assert.Empty(await a.Raw.GetOkAsync<List<RequestDto>>($"/workspaces/{ws}/requests"));
        Assert.Equal(1, (await a.Sync.Engine.GetStatusAsync()).Pending);
    }
}
