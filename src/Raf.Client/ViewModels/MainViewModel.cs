using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Controls;
using Raf.Client.Core.Api;
using Raf.Client.Core.Diagnostics;
using Raf.Client.Core.Execution;
using Raf.Client.Core.History;
using Raf.Client.Core.Sync;
using Raf.Client.Core.Workspaces;
using Raf.Client.Services;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Client.ViewModels;

public enum NodeKind { App, Folder, Request, Sample }

public sealed partial class TreeNodeViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Method))]
    private object _dto;

    [ObservableProperty] private bool _isExpanded;

    public TreeNodeViewModel(MainViewModel owner, NodeKind kind, object dto, TreeNodeViewModel? parent)
    {
        _owner = owner;
        Kind = kind;
        _dto = dto;
        Parent = parent;
    }

    public NodeKind Kind { get; }
    public TreeNodeViewModel? Parent { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public AppDto App => (AppDto)Dto;
    public FolderDto Folder => (FolderDto)Dto;
    public RequestDto Request => (RequestDto)Dto;
    public SampleDto Sample => (SampleDto)Dto;

    public Guid Id => Dto switch
    {
        AppDto a => a.Id,
        FolderDto f => f.Id,
        RequestDto r => r.Id,
        SampleDto s => s.Id,
        _ => Guid.Empty,
    };

    public string Name => Dto switch
    {
        AppDto a => a.Name,
        FolderDto f => f.Name,
        RequestDto r => r.Name,
        SampleDto s => s.Name,
        _ => "",
    };

    public int Order => Dto switch
    {
        AppDto a => a.Order,
        FolderDto f => f.Order,
        RequestDto r => r.Order,
        SampleDto s => s.Order,
        _ => 0,
    };

    /// <summary>Método del request; null en los demás nodos. La vista elige el icono según el tipo.</summary>
    public string? Method => Dto is RequestDto r ? r.Spec.Method : null;

    public bool IsApp => Kind == NodeKind.App;
    public bool IsFolder => Kind == NodeKind.Folder;
    public bool IsRequest => Kind == NodeKind.Request;
    public bool IsSample => Kind == NodeKind.Sample;

    public bool CanEdit => _owner.CanEdit;
    public bool CanAddContainer => CanEdit && Kind is NodeKind.App or NodeKind.Folder;
    public bool CanAddSample => CanEdit && Kind == NodeKind.Request;

    public IEnumerable<TreeNodeViewModel> SelfAndDescendants() =>
        Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    [RelayCommand] private Task NewFolder() => _owner.NewFolderAsync(this);
    [RelayCommand] private Task NewRequest() => _owner.NewRequestAsync(this);
    [RelayCommand] private Task NewSample() => _owner.NewSampleAsync(this);
    [RelayCommand] private Task Rename() => _owner.RenameAsync(this);
    [RelayCommand] private Task Delete() => _owner.DeleteAsync(this);
    [RelayCommand] private Task MoveUp() => _owner.MoveAsync(this, -1);
    [RelayCommand] private Task MoveDown() => _owner.MoveAsync(this, 1);
}

public sealed record EnvironmentChoice(Guid? Id, string Name);

public sealed record HistoryRow(RunHistoryEntry Entry)
{
    public string Title => $"{Entry.Method} {Entry.Name}";
    public string Method => Entry.Method;
    public string Name => Entry.Name;
    public string Url => Entry.Url;
    public string Result =>
        (Entry.StatusCode?.ToString() ?? ResponseViewModel.OutcomeText(Entry.Outcome))
        + $" · {Entry.ElapsedMs} ms · {Entry.At.LocalDateTime:g}";
}

public sealed partial class MainViewModel : ObservableObject
{
    private IReadOnlyList<VariableDto> _variables = [];
    private bool _switching;
    private bool _reloadRunning;
    private bool _reloadAgain;
    private bool _roleChanged;
    private readonly SyncSession _sync;
    private readonly ManagementApi _management;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Permissions), nameof(CanEdit), nameof(RoleText))]
    [NotifyCanExecuteChangedFor(nameof(NewAppCommand), nameof(OpenWorkspaceSettingsCommand))]
    private WorkspaceDto? _selectedWorkspace;

    [ObservableProperty] private EnvironmentChoice? _selectedEnvironment;
    [ObservableProperty] private EditorTabViewModel? _selectedTab;
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncText), nameof(SyncDetail), nameof(IsSynced), nameof(IsOffline), nameof(IsInConflict), nameof(IsSyncPending))]
    [NotifyCanExecuteChangedFor(nameof(OpenConflictsCommand))]
    private SyncStatus _syncStatus = new(SyncState.Pending, 0, 0);

    public MainViewModel(ClientServices services, ShellViewModel shell, SyncSession sync)
    {
        Services = services;
        Shell = shell;
        _sync = sync;
        _management = new ManagementApi(services.Api);
        sync.Engine.StatusChanged += s => Dispatcher.UIThread.Post(() => SyncStatus = s);
        sync.Engine.DataChanged += () => Dispatcher.UIThread.Post(() => _ = ReloadFromSyncAsync());
        sync.Engine.WorkspaceLost += lost => Dispatcher.UIThread.Post(() => _ = OnWorkspaceLostAsync(lost));
    }

    public string SyncText => SyncStatus.State switch
    {
        SyncState.Synced => "Sincronizado",
        SyncState.Offline when SyncStatus.Pending > 0 => $"Sin conexión · {SyncStatus.Pending} pendientes",
        SyncState.Offline => "Sin conexión",
        SyncState.Conflict => $"{SyncStatus.Conflicts} en conflicto",
        _ when SyncStatus.Pending > 0 => $"{SyncStatus.Pending} pendientes",
        _ => "Sincronizando…",
    };

    /// <summary>Tooltip del indicador: contra qué servidor, cuándo sincronizó y por qué falló.</summary>
    public string SyncDetail
    {
        get
        {
            var lines = new List<string> { $"Servidor: {Services.Settings.Current.ServerUrl}" };
            lines.Add(SyncStatus.LastSyncedAt is { } at
                ? $"Última sincronización: {at:HH:mm:ss}"
                : "Todavía no se sincronizó en esta sesión.");
            if (SyncStatus.LastError is { } error) lines.Add($"Último error: {error}");
            if (SyncStatus.State == SyncState.Offline) lines.Add("Se reintenta cada 15 segundos.");
            if (HasConflicts) lines.Add("Hacé clic para resolver los conflictos.");
            lines.Add("El detalle está en la consola de eventos (Ctrl+Shift+L).");
            return string.Join(Environment.NewLine, lines);
        }
    }

    // Estado para la vista, que decide el aspecto con clases.
    public bool IsSynced => SyncStatus.State == SyncState.Synced;
    public bool IsOffline => SyncStatus.State == SyncState.Offline;
    public bool IsInConflict => SyncStatus.State == SyncState.Conflict;
    public bool IsSyncPending => !IsSynced && !IsOffline && !IsInConflict;

    public void OnWindowActivated() => _sync.Engine.Kick();

    /// <summary>Lo implementa la vista, que abre la ventana de conflictos.</summary>
    public Func<ConflictsViewModel, Task>? ShowConflicts { get; set; }

    private bool HasConflicts => SyncStatus.Conflicts > 0;

    [RelayCommand(CanExecute = nameof(HasConflicts))]
    private Task OpenConflictsAsync() => GuardAsync(async () =>
    {
        if (ShowConflicts is null) return;
        var vm = new ConflictsViewModel(_sync, Services.Dialogs);
        await vm.LoadAsync();
        await ShowConflicts(vm);
        await ReloadFromSyncAsync();
    });

    /// <summary>Refleja lo que trajo la sincronización. Los pedidos que llegan durante una recarga se atienden con una más.</summary>
    private async Task ReloadFromSyncAsync()
    {
        if (_reloadRunning)
        {
            _reloadAgain = true;
            return;
        }

        _reloadRunning = true;
        try
        {
            do
            {
                _reloadAgain = false;
                await GuardAsync(RefreshWorkspacesAsync);
                if (SelectedWorkspace is not null) await ReloadAsync();
            }
            while (_reloadAgain);
        }
        finally
        {
            _reloadRunning = false;
        }
    }

    private async Task RefreshWorkspacesAsync()
    {
        var list = await Services.Store.GetWorkspacesAsync();
        var selectedId = SelectedWorkspace?.Id;
        var previousRole = SelectedWorkspace?.MyRole;

        // Reemplazar la lista no es un cambio de workspace: el combo se vacía y vuelve a la misma selección.
        _switching = true;
        Workspaces.Clear();
        foreach (var w in list) Workspaces.Add(w);
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == selectedId);
        _switching = false;

        if (SelectedWorkspace is { } current && current.MyRole != previousRole)
        {
            // Las pestañas abiertas se armaron con el rol anterior: se rehacen en la próxima recarga.
            _roleChanged = true;
            ReportStatus($"Tu rol en \"{current.Name}\" cambió: ahora sos {Permissions.RoleText}.");
        }

        if (SelectedWorkspace is null && Workspaces.Count > 0) SelectedWorkspace = Workspaces[0];
        else if (SelectedWorkspace is null && selectedId is not null) await SwitchWorkspaceAsync(null);
    }

    private async Task OnWorkspaceLostAsync(WorkspaceLost lost)
    {
        if (lost.PendingChanges > 0)
        {
            await Services.Dialogs.InfoAsync(
                "Perdiste el acceso a un workspace",
                $"Ya no tenés acceso a \"{lost.Name}\". Se van a descartar {lost.PendingChanges} cambios tuyos que no se llegaron a enviar.");
            await GuardAsync(() => _sync.Store.ForgetWorkspaceAsync(lost.Id));
        }
        else
        {
            ReportStatus($"Ya no tenés acceso a \"{lost.Name}\".");
        }
        await ReloadFromSyncAsync();
    }

    public ClientServices Services { get; }
    public ShellViewModel Shell { get; }
    public VariableLookup VariableLookup { get; } = new();

    public ObservableCollection<WorkspaceDto> Workspaces { get; } = [];
    public ObservableCollection<EnvironmentChoice> Environments { get; } = [];
    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];
    public ObservableCollection<EditorTabViewModel> Tabs { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];

    public VariableContext Variables { get; private set; } = VariableContext.Empty;

    public Guid WorkspaceId => SelectedWorkspace?.Id ?? Guid.Empty;

    /// <summary>Qué se muestra y se habilita según el rol. El servidor valida igual cada acción.</summary>
    public WorkspacePermissions Permissions => WorkspacePermissions.For(SelectedWorkspace, IsAdmin);

    public bool CanEdit => Permissions.CanEditContent;
    public bool CanSeeSecrets => Permissions.CanSeeSecrets;
    public bool IsAdmin => Services.Session.User?.IsAdmin == true;
    public string UserText => Services.Session.User?.DisplayName ?? "";
    public string RoleText => Permissions.RoleText;

    public bool HasUnsavedChanges => Tabs.Any(t => t.IsDirty);

    public void ReportStatus(string message) => Status = message;

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ClientOutdatedException)
        {
            // El shell ya muestra la pantalla de actualización y lo anota en la consola.
        }
        catch (SessionExpiredException)
        {
            // La sesión ya lo anotó y la app vuelve al login.
        }
        catch (ApiException e)
        {
            ReportStatus(e.Message);
            var status = e.Status is { } code ? $" (HTTP {(int)code})" : "";
            Services.Events.Warning(EventCategory.App, $"Falló una operación{status}: {e.Message}", e.InnerException?.ToString());
        }
    }

    public Task InitializeAsync() => GuardAsync(async () =>
    {
        await ReloadHistoryAsync();
        var list = await Services.Store.GetWorkspacesAsync();
        Workspaces.Clear();
        foreach (var w in list) Workspaces.Add(w);

        var last = Services.Settings.Current.LastWorkspaceId;
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == last) ?? Workspaces.FirstOrDefault();
        if (SelectedWorkspace is null) ReportStatus("Creá tu primer workspace con el botón + de arriba.");
        SyncStatus = await _sync.Engine.GetStatusAsync();
        _sync.Engine.Start();
    });

    private WorkspaceDto? _previousWorkspace;

    partial void OnSelectedWorkspaceChanged(WorkspaceDto? oldValue, WorkspaceDto? newValue)
    {
        if (_switching) return;
        _previousWorkspace = oldValue;
        _ = SwitchWorkspaceAsync(newValue);
    }

    private async Task SwitchWorkspaceAsync(WorkspaceDto? workspace)
    {
        if (HasUnsavedChanges && !await Services.Dialogs.ConfirmAsync(
                "Cambios sin guardar", "Si cambiás de workspace se pierden los cambios sin guardar.", "Cambiar"))
        {
            // Se difiere para que el ComboBox termine de procesar la selección actual.
            Dispatcher.UIThread.Post(() =>
            {
                _switching = true;
                SelectedWorkspace = _previousWorkspace;
                _switching = false;
            });
            return;
        }

        CloseAllTabs();
        Roots.Clear();
        SelectedEnvironment = null; // el environment elegido pertenece al workspace anterior
        Environments.Clear();
        Status = null;
        if (workspace is null) return;

        Services.Settings.Update(s => s.LastWorkspaceId = workspace.Id);
        await ReloadAsync();
    }

    private void CloseAllTabs()
    {
        foreach (var tab in Tabs) tab.Dispose();
        Tabs.Clear();
    }

    [RelayCommand]
    private Task RefreshAsync() => GuardAsync(async () =>
    {
        await _sync.Engine.SyncOnceAsync();
        await ReloadAsync();
    });

    private async Task ReloadAsync()
    {
        var ws = WorkspaceId;
        await GuardAsync(async () =>
        {
            await ReloadVariablesAsync();

            var apps = await Services.Store.GetAppsAsync(ws);
            var folders = await Services.Store.GetFoldersAsync(ws);
            var requests = await Services.Store.GetRequestsAsync(ws);
            var samples = await Services.Store.GetSamplesAsync(ws);
            if (ws != WorkspaceId) return;

            var expanded = Roots.SelectMany(r => r.SelfAndDescendants()).Where(n => n.IsExpanded).Select(n => n.Id).ToHashSet();
            var nodes = BuildTree(apps, folders, requests, samples);
            Roots.Clear();
            foreach (var root in nodes) Roots.Add(root);
            foreach (var n in Roots.SelectMany(r => r.SelfAndDescendants())) n.IsExpanded = expanded.Contains(n.Id);
            RebindOpenTabs(recreate: _roleChanged);
            _roleChanged = false;
        });
    }

    private List<TreeNodeViewModel> BuildTree(
        IReadOnlyList<AppDto> apps, IReadOnlyList<FolderDto> folders,
        IReadOnlyList<RequestDto> requests, IReadOnlyList<SampleDto> samples)
    {
        var result = new List<TreeNodeViewModel>();
        foreach (var app in apps)
        {
            var appNode = new TreeNodeViewModel(this, NodeKind.App, app, null) { IsExpanded = false };
            AddChildren(appNode, null, app.Id);
            result.Add(appNode);
        }
        return result;

        void AddChildren(TreeNodeViewModel parent, Guid? folderId, Guid appId)
        {
            foreach (var f in folders.Where(x => x.AppId == appId && x.ParentFolderId == folderId))
            {
                var node = new TreeNodeViewModel(this, NodeKind.Folder, f, parent);
                AddChildren(node, f.Id, appId);
                parent.Children.Add(node);
            }

            foreach (var r in requests.Where(x => x.AppId == appId && x.FolderId == folderId))
            {
                var node = new TreeNodeViewModel(this, NodeKind.Request, r, parent);
                foreach (var s in samples.Where(x => x.RequestId == r.Id))
                    node.Children.Add(new TreeNodeViewModel(this, NodeKind.Sample, s, node));
                parent.Children.Add(node);
            }
        }
    }

    /// <summary>Tras recargar el árbol, las pestañas abiertas sin cambios se reemplazan; las eliminadas se cierran.</summary>
    /// <param name="recreate">Rehace las pestañas sin cambios aunque su contenido no haya cambiado (cambió el rol).</param>
    private void RebindOpenTabs(bool recreate = false)
    {
        var byId = Roots.SelectMany(r => r.SelfAndDescendants()).ToDictionary(n => n.Id);
        foreach (var tab in Tabs.ToList())
        {
            if (!byId.TryGetValue(tab.Node.Id, out var fresh))
            {
                if (tab.IsDirty) continue; // se conserva para no perder lo escrito; guardar informará el error
                tab.Dispose();
                Tabs.Remove(tab);
                continue;
            }

            // Con cambios sin guardar conserva la versión que tenía: al guardar, el conflicto se detecta y se resuelve.
            if (tab.IsDirty) continue;

            if (!recreate && SyncContent.Same(tab.Node.Dto, fresh.Dto))
            {
                tab.Rebind(fresh); // no se recrea, para no perder la respuesta que está mostrando
                continue;
            }

            var index = Tabs.IndexOf(tab);
            var selected = SelectedTab == tab;
            tab.Dispose();
            Tabs[index] = CreateTab(fresh);
            if (selected) SelectedTab = Tabs[index];
        }
    }

    private async Task ReloadVariablesAsync()
    {
        var ws = WorkspaceId;
        var environments = await Services.Store.GetEnvironmentsAsync(ws);
        _variables = await Services.Store.GetVariablesAsync(ws);

        var saved = Services.Settings.Current.ActiveEnvironments.TryGetValue(ws, out var id) ? id : (Guid?)null;
        var keep = SelectedEnvironment?.Id ?? saved;

        Environments.Clear();
        Environments.Add(new EnvironmentChoice(null, "Sin environment"));
        foreach (var e in environments) Environments.Add(new EnvironmentChoice(e.Id, e.Name));
        SelectedEnvironment = Environments.FirstOrDefault(e => e.Id == keep) ?? Environments[0];
        ApplyVariables();
    }

    partial void OnSelectedEnvironmentChanged(EnvironmentChoice? value)
    {
        if (value is null) return;
        var ws = WorkspaceId;
        Services.Settings.Update(s =>
        {
            if (value.Id is { } id) s.ActiveEnvironments[ws] = id;
            else s.ActiveEnvironments.Remove(ws);
        });
        ApplyVariables();
    }

    private void ApplyVariables()
    {
        Variables = VariableContext.From(_variables, SelectedEnvironment?.Id);
        VariableLookup.Update(Variables);
        foreach (var tab in Tabs) tab.RefreshWarning();
    }

    private EditorTabViewModel CreateTab(TreeNodeViewModel node) =>
        node.Kind == NodeKind.Request ? new RequestTabViewModel(this, node) : new SampleTabViewModel(this, node);

    public void Open(TreeNodeViewModel node)
    {
        if (node.Kind is not (NodeKind.Request or NodeKind.Sample)) return;

        var tab = Tabs.FirstOrDefault(t => t.Node.Id == node.Id);
        if (tab is null)
        {
            tab = CreateTab(node);
            Tabs.Add(tab);
        }
        SelectedTab = tab;
    }

    public void OpenFromHistory(HistoryRow row)
    {
        var id = row.Entry.SampleId ?? row.Entry.RequestId;
        var node = Roots.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == id);
        if (node is null) ReportStatus("Ese elemento ya no está en este workspace.");
        else Open(node);
    }

    public async Task CloseTabAsync(EditorTabViewModel tab)
    {
        if (tab.IsDirty && !await Services.Dialogs.ConfirmAsync(
                "Cambios sin guardar", $"\"{tab.Node.Name}\" tiene cambios sin guardar. ¿Cerrar y descartarlos?", "Descartar"))
            return;
        tab.Dispose();
        Tabs.Remove(tab);
    }

    public void NotifyRequestChanged(TreeNodeViewModel request)
    {
        foreach (var tab in Tabs.Where(t => t.Node.Parent == request && t is SampleTabViewModel)) tab.OnParentChanged();
    }

    public async Task AddHistoryAsync(RunHistoryEntry entry)
    {
        if (Services.Session.User is not { } user) return;
        await Services.History.AddAsync(user.Id, entry);
        await ReloadHistoryAsync();
    }

    private async Task ReloadHistoryAsync()
    {
        if (Services.Session.User is not { } user) return;
        var entries = await Services.History.GetAsync(user.Id);
        History.Clear();
        foreach (var e in entries) History.Add(new HistoryRow(e));
    }

    // ---------- Workspaces y árbol ----------

    [RelayCommand]
    private Task NewWorkspaceAsync() => GuardAsync(async () =>
    {
        var name = await Services.Dialogs.PromptAsync("Nuevo workspace", "Nombre");
        if (name is null) return;
        var created = await Services.Store.CreateWorkspaceAsync(name);
        Workspaces.Add(created);
        SelectedWorkspace = created;
    });

    private bool HasWorkspaceToEdit => CanEdit;

    [RelayCommand(CanExecute = nameof(HasWorkspaceToEdit))]
    private Task NewAppAsync() => GuardAsync(async () =>
    {
        var name = await Services.Dialogs.PromptAsync("Nueva app", "Nombre");
        if (name is null) return;
        var order = NextOrder(Roots, NodeKind.App);
        var dto = await Services.Store.CreateAppAsync(WorkspaceId, new CreateAppRequest(name, order));
        Roots.Add(new TreeNodeViewModel(this, NodeKind.App, dto, null));
    });

    private static int NextOrder(IEnumerable<TreeNodeViewModel> siblings, NodeKind kind) =>
        siblings.Where(n => n.Kind == kind).Select(n => n.Order + 1).DefaultIfEmpty(0).Max();

    private static (Guid AppId, Guid? FolderId) Container(TreeNodeViewModel node) =>
        node.Kind == NodeKind.App ? (node.Id, null) : (node.Folder.AppId, node.Id);

    private static void InsertSorted(TreeNodeViewModel parent, TreeNodeViewModel child)
    {
        // Las carpetas van antes que los requests.
        var index = child.Kind == NodeKind.Folder
            ? parent.Children.Count(c => c.Kind == NodeKind.Folder)
            : parent.Children.Count;
        parent.Children.Insert(index, child);
        parent.IsExpanded = true;
    }

    public Task NewFolderAsync(TreeNodeViewModel parent) => GuardAsync(async () =>
    {
        var name = await Services.Dialogs.PromptAsync("Nueva carpeta", "Nombre");
        if (name is null) return;
        var (appId, folderId) = Container(parent);
        var dto = await Services.Store.CreateFolderAsync(WorkspaceId, new CreateFolderRequest(
            appId, name, folderId, NextOrder(parent.Children, NodeKind.Folder)));
        InsertSorted(parent, new TreeNodeViewModel(this, NodeKind.Folder, dto, parent));
    });

    public Task NewRequestAsync(TreeNodeViewModel parent) => GuardAsync(async () =>
    {
        var name = await Services.Dialogs.PromptAsync("Nuevo request", "Nombre");
        if (name is null) return;
        var (appId, folderId) = Container(parent);
        var dto = await Services.Store.CreateRequestAsync(WorkspaceId, new CreateRequestRequest(
            appId, name, null, folderId, NextOrder(parent.Children, NodeKind.Request)));
        var node = new TreeNodeViewModel(this, NodeKind.Request, dto, parent);
        InsertSorted(parent, node);
        Open(node);
    });

    public Task NewSampleAsync(TreeNodeViewModel request) => GuardAsync(async () =>
    {
        var name = await Services.Dialogs.PromptAsync("Nuevo sample", "Nombre");
        if (name is null) return;
        var dto = await Services.Store.CreateSampleAsync(WorkspaceId, new CreateSampleRequest(
            request.Id, name, null, NextOrder(request.Children, NodeKind.Sample)));
        var node = new TreeNodeViewModel(this, NodeKind.Sample, dto, request);
        InsertSorted(request, node);
        Open(node);
    });

    public Task RenameAsync(TreeNodeViewModel node) => GuardAsync(async () =>
    {
        var name = await Services.Dialogs.PromptAsync("Renombrar", "Nombre", node.Name);
        if (name is null || name == node.Name) return;
        await UpdateNodeAsync(node, name, node.Order);
    });

    /// <summary>
    /// Cambia nombre u orden usando el último estado guardado. Las pestañas leen la versión del nodo, así que
    /// sus cambios sin guardar siguen siendo válidos.
    /// </summary>
    private async Task UpdateNodeAsync(TreeNodeViewModel node, string name, int order)
    {
        var store = Services.Store;
        var ws = WorkspaceId;
        node.Dto = node.Dto switch
        {
            AppDto a => await store.UpdateAppAsync(ws, a.Id, new UpdateAppRequest(name, order, a.Version)),
            FolderDto f => await store.UpdateFolderAsync(ws, f.Id, new UpdateFolderRequest(name, f.ParentFolderId, order, f.Version)),
            RequestDto r => await store.UpdateRequestAsync(ws, r.Id, new UpdateRequestRequest(name, r.FolderId, order, r.Spec, r.Version)),
            SampleDto s => await store.UpdateSampleAsync(ws, s.Id, new UpdateSampleRequest(name, order, s.Overrides, s.Version)),
            var other => other,
        };
    }

    public Task DeleteAsync(TreeNodeViewModel node) => GuardAsync(async () =>
    {
        if (!await Services.Dialogs.ConfirmAsync(
                "Eliminar", $"¿Eliminar \"{node.Name}\" y todo lo que contiene?", "Eliminar"))
            return;

        var store = Services.Store;
        var ws = WorkspaceId;
        await (node.Kind switch
        {
            NodeKind.App => store.DeleteAppAsync(ws, node.Id),
            NodeKind.Folder => store.DeleteFolderAsync(ws, node.Id),
            NodeKind.Request => store.DeleteRequestAsync(ws, node.Id),
            _ => store.DeleteSampleAsync(ws, node.Id),
        });

        var removed = node.SelfAndDescendants().ToHashSet();
        foreach (var tab in Tabs.Where(t => removed.Contains(t.Node)).ToList())
        {
            tab.Dispose();
            Tabs.Remove(tab);
        }
        (node.Parent?.Children ?? Roots).Remove(node);
    });

    /// <summary>Mueve el nodo entre sus hermanos del mismo tipo y persiste el orden resultante.</summary>
    public Task MoveAsync(TreeNodeViewModel node, int delta) => GuardAsync(async () =>
    {
        var list = node.Parent?.Children ?? Roots;
        var siblings = list.Where(n => n.Kind == node.Kind).ToList();
        var from = siblings.IndexOf(node);
        var to = from + delta;
        if (to < 0 || to >= siblings.Count) return;

        var listTarget = list.IndexOf(siblings[to]);
        siblings[from] = siblings[to];
        siblings[to] = node;

        for (var i = 0; i < siblings.Count; i++)
            if (siblings[i].Order != i)
                await UpdateNodeAsync(siblings[i], siblings[i].Name, i);

        list.Move(list.IndexOf(node), listTarget);
    });

    // ---------- Sesión y variables ----------

    [RelayCommand]
    private void ChangePassword() => Shell.ShowPasswordChange(this);

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (HasUnsavedChanges && !await Services.Dialogs.ConfirmAsync(
                "Cambios sin guardar", "Si cerrás sesión se pierden los cambios sin guardar.", "Cerrar sesión"))
            return;
        CloseAllTabs();
        await Services.Session.LogoutAsync();
        Shell.ShowLogin();
    }

    /// <summary>Lo implementa la vista, que abre la ventana de environments.</summary>
    public Func<EnvironmentsViewModel, Task>? ShowEnvironments { get; set; }

    [RelayCommand]
    private Task EditEnvironmentsAsync() => GuardAsync(async () =>
    {
        if (SelectedWorkspace is null || ShowEnvironments is null) return;
        var vm = new EnvironmentsViewModel(Services, WorkspaceId, CanEdit);
        await vm.LoadAsync();
        await ShowEnvironments(vm);
        await ReloadVariablesAsync();
    });

    // ---------- Gestión del workspace y del servidor ----------

    /// <summary>Lo implementa la vista, que abre la ventana del workspace.</summary>
    public Func<WorkspaceSettingsViewModel, Task>? ShowWorkspaceSettings { get; set; }

    /// <summary>Lo implementa la vista, que abre la ventana de administración.</summary>
    public Func<AdminViewModel, Task>? ShowAdmin { get; set; }

    private bool HasWorkspace => SelectedWorkspace is not null;

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private Task OpenWorkspaceSettingsAsync() => GuardAsync(async () =>
    {
        if (SelectedWorkspace is not { } workspace || ShowWorkspaceSettings is null) return;
        var vm = new WorkspaceSettingsViewModel(_management, Services.Dialogs, workspace, Services.Session.User!.Id, IsAdmin)
        {
            Events = Services.Events,
        };
        await vm.LoadAsync();
        await ShowWorkspaceSettings(vm);

        switch (vm.Outcome)
        {
            case WorkspaceSettingsOutcome.Gone:
                // Ya no existe o ya no es miembro: lo local de ese workspace no sirve más.
                if (SelectedWorkspace?.Id == workspace.Id) CloseAllTabs();
                await _sync.Store.ForgetWorkspaceAsync(workspace.Id);
                await ReloadFromSyncAsync();
                break;
            case WorkspaceSettingsOutcome.Changed:
                await SyncNowAsync();
                break;
        }
    });

    [RelayCommand]
    private Task OpenAdminAsync() => GuardAsync(async () =>
    {
        if (ShowAdmin is null) return;
        var vm = new AdminViewModel(_management, Services.Dialogs, Services.Session.User!.Id) { Events = Services.Events };
        await vm.LoadAsync();
        await ShowAdmin(vm);
        if (vm.Changed) await SyncNowAsync();
    });

    private async Task SyncNowAsync()
    {
        await _sync.Engine.SyncOnceAsync();
        await ReloadFromSyncAsync();
    }
}
