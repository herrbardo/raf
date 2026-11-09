using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Workspaces;
using Raf.Client.Services;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Client.ViewModels;

/// <summary>Cómo quedó el workspace al cerrar la ventana.</summary>
public enum WorkspaceSettingsOutcome { Unchanged, Changed, Gone }

public sealed partial class MemberRowViewModel : ObservableObject
{
    private readonly WorkspaceSettingsViewModel _owner;
    private bool _loading;

    [ObservableProperty] private WorkspaceRole _role;

    public MemberRowViewModel(WorkspaceSettingsViewModel owner, MemberDto member, bool isMe)
    {
        _owner = owner;
        Member = member;
        IsMe = isMe;
        _loading = true;
        Role = member.Role;
        _loading = false;
    }

    public MemberDto Member { get; private set; }
    public bool IsMe { get; }
    public string Name => IsMe ? $"{Member.DisplayName} (vos)" : Member.DisplayName;
    public string Email => Member.Email;
    public string RoleText => Member.Role.ToString();

    public bool CanChangeRole => _owner.Permissions.CanChangeRoleOf(Member);
    public bool ShowRoleText => !CanChangeRole;
    public bool CanRemove => _owner.Permissions.CanRemove(Member);
    public bool CanTransfer => _owner.Permissions.CanTransferTo(Member);

    public static WorkspaceRole[] AssignableRoles { get; } = [WorkspaceRole.Editor, WorkspaceRole.Viewer];

    /// <summary>Vuelve al rol guardado sin disparar otro cambio (por ejemplo, si el servidor lo rechazó).</summary>
    internal void Reset(MemberDto member)
    {
        Member = member;
        _loading = true;
        Role = member.Role;
        _loading = false;
        OnPropertyChanged(nameof(RoleText));
    }

    partial void OnRoleChanged(WorkspaceRole oldValue, WorkspaceRole newValue)
    {
        if (_loading || newValue == Member.Role) return;
        _ = _owner.ChangeRoleAsync(this, newValue);
    }

    [RelayCommand] private Task Remove() => _owner.RemoveAsync(this);
    [RelayCommand] private Task Transfer() => _owner.TransferAsync(this);
}

/// <summary>Nombre y miembros de un workspace. Todos ven los miembros; solo el Owner los gestiona.</summary>
public sealed partial class WorkspaceSettingsViewModel : PageViewModel
{
    private readonly ManagementApi _api;
    private readonly IDialogs _dialogs;
    private readonly Guid _myUserId;
    private WorkspaceDto _workspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    private string? _name;

    [ObservableProperty] private string? _search;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMemberCommand))]
    private UserSummaryDto? _candidate;

    [ObservableProperty] private WorkspaceRole _newMemberRole = WorkspaceRole.Editor;

    public WorkspaceSettingsViewModel(ManagementApi api, IDialogs dialogs, WorkspaceDto workspace, Guid myUserId, bool isAdmin)
    {
        _api = api;
        _dialogs = dialogs;
        _workspace = workspace;
        _myUserId = myUserId;
        _name = workspace.Name;
        Permissions = WorkspacePermissions.For(workspace, isAdmin);
    }

    public WorkspacePermissions Permissions { get; private set; }
    public string Title => $"Workspace \"{_workspace.Name}\"";
    public string RoleText => $"Tu rol: {Permissions.RoleText}";
    public ObservableCollection<MemberRowViewModel> Members { get; } = [];
    public ObservableCollection<UserSummaryDto> Candidates { get; } = [];
    public WorkspaceSettingsOutcome Outcome { get; private set; }

    /// <summary>La ventana se cierra sola, por ejemplo al salir del workspace o eliminarlo.</summary>
    public event Action? CloseRequested;

    public Task LoadAsync() => GuardAsync(async () =>
    {
        _workspace = await _api.GetWorkspaceAsync(_workspace.Id);
        Permissions = Permissions with { Role = _workspace.MyRole };
        Name = _workspace.Name;
        OnPropertyChanged(nameof(Permissions));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(RoleText));
        await LoadMembersAsync();
    });

    private async Task LoadMembersAsync()
    {
        Members.Clear();
        foreach (var m in await _api.GetMembersAsync(_workspace.Id)) Members.Add(new MemberRowViewModel(this, m, m.UserId == _myUserId));
        RefreshCandidates();
    }

    private void MarkChanged()
    {
        if (Outcome == WorkspaceSettingsOutcome.Unchanged) Outcome = WorkspaceSettingsOutcome.Changed;
    }

    // ---------- Nombre y eliminación ----------

    private bool CanRename => Permissions.CanRenameWorkspace && !string.IsNullOrWhiteSpace(Name) && Name.Trim() != _workspace.Name;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private Task RenameAsync() => GuardAsync(async () =>
    {
        _workspace = await _api.RenameWorkspaceAsync(_workspace.Id, Name!.Trim(), _workspace.Version);
        Name = _workspace.Name;
        OnPropertyChanged(nameof(Title));
        RenameCommand.NotifyCanExecuteChanged();
        MarkChanged();
    });

    [RelayCommand]
    private Task DeleteAsync() => GuardAsync(async () =>
    {
        var typed = await _dialogs.PromptAsync(
            "Eliminar workspace",
            $"Se elimina \"{_workspace.Name}\" con todo su contenido, para todos sus miembros. " +
            "Para confirmar, escribí el nombre del workspace.");
        if (typed is null) return;
        if (typed != _workspace.Name)
        {
            Error = "El nombre no coincide: no se eliminó el workspace.";
            return;
        }

        await _api.DeleteWorkspaceAsync(_workspace.Id);
        Outcome = WorkspaceSettingsOutcome.Gone;
        CloseRequested?.Invoke();
    });

    [RelayCommand]
    private Task LeaveAsync() => GuardAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Salir del workspace",
                $"Dejás de ver \"{_workspace.Name}\". Para volver, el Owner te tiene que agregar de nuevo.", "Salir"))
            return;

        await _api.RemoveMemberAsync(_workspace.Id, _myUserId);
        Outcome = WorkspaceSettingsOutcome.Gone;
        CloseRequested?.Invoke();
    });

    // ---------- Miembros ----------

    partial void OnSearchChanged(string? value) => _ = SearchUsersAsync(value);

    private async Task SearchUsersAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
        {
            Candidates.Clear();
            return;
        }

        await GuardAsync(async () =>
        {
            var found = await _api.SearchUsersAsync(text);
            if (text != Search) return; // llegó tarde: ya se escribió otra cosa
            Candidates.Clear();
            foreach (var u in found.Where(u => Members.All(m => m.Member.UserId != u.Id))) Candidates.Add(u);
            Candidate = Candidates.FirstOrDefault();
        });
    }

    private void RefreshCandidates()
    {
        foreach (var c in Candidates.Where(c => Members.Any(m => m.Member.UserId == c.Id)).ToList()) Candidates.Remove(c);
        if (Candidate is not null && !Candidates.Contains(Candidate)) Candidate = Candidates.FirstOrDefault();
    }

    private bool CanAdd => Permissions.CanManageMembers && Candidate is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddMemberAsync() => GuardAsync(async () =>
    {
        await _api.AddMemberAsync(_workspace.Id, Candidate!.Id, NewMemberRole);
        MarkChanged();
        await LoadMembersAsync();
    });

    internal Task ChangeRoleAsync(MemberRowViewModel row, WorkspaceRole role) => GuardAsync(async () =>
    {
        try
        {
            await _api.ChangeRoleAsync(_workspace.Id, row.Member.UserId, role);
            row.Reset(row.Member with { Role = role });
            MarkChanged();
        }
        catch
        {
            row.Reset(row.Member);
            throw;
        }
    });

    internal Task RemoveAsync(MemberRowViewModel row) => GuardAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Quitar miembro",
                $"{row.Member.DisplayName} deja de tener acceso a \"{_workspace.Name}\".", "Quitar"))
            return;
        await _api.RemoveMemberAsync(_workspace.Id, row.Member.UserId);
        MarkChanged();
        await LoadMembersAsync();
    });

    internal Task TransferAsync(MemberRowViewModel row) => GuardAsync(async () =>
    {
        if (!await _dialogs.ConfirmAsync("Transferir la propiedad",
                $"{row.Member.DisplayName} pasa a ser el Owner de \"{_workspace.Name}\" y vos quedás como Editor. " +
                "Ya no vas a poder gestionar miembros ni eliminar el workspace.", "Transferir"))
            return;
        await _api.TransferOwnershipAsync(_workspace.Id, row.Member.UserId);
        MarkChanged();
        await LoadAsync();
    });
}
