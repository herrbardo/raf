using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Workspaces;
using Raf.Client.Services;
using Raf.Contracts;

namespace Raf.Client.ViewModels;

public sealed record UserRow(UserDto User)
{
    public string Name => User.DisplayName;
    public string Email => User.Email;

    /// <summary>Estado en texto, para no depender solo del color.</summary>
    public string Badges => string.Join(" · ", new[]
    {
        User.IsAdmin ? Texts.Admin_BadgeAdmin : null,
        User.IsDisabled ? Texts.Admin_BadgeDisabled : null,
        User.MustChangePassword ? Texts.Admin_BadgeTempPassword : null,
    }.Where(x => x is not null));

    public bool Matches(string? text) =>
        string.IsNullOrWhiteSpace(text)
        || User.DisplayName.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase)
        || User.Email.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase);
}

public sealed record AdminWorkspaceRow(AdminWorkspaceDto Workspace)
{
    public string Name => Workspace.Name;
    public string OwnerText => Workspace.Owner is { } o ? string.Format(Texts.Admin_Owner, o.DisplayName, o.Email) : Texts.Admin_NoOwner;
    public string MembersText => Workspace.MemberCount == 1 ? Texts.Admin_MemberOne : string.Format(Texts.Admin_MemberMany, Workspace.MemberCount);
}

/// <summary>Gestión del servidor: usuarios y workspaces. Solo para Admin; el servidor lo valida igual.</summary>
public sealed partial class AdminViewModel(ManagementApi api, IDialogs dialogs, Guid myUserId) : PageViewModel
{
    private List<UserRow> _allUsers = [];

    // ---------- Usuarios ----------

    [ObservableProperty] private string? _search;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsEditing), nameof(EditorTitle), nameof(CanToggleDisabled),
        nameof(ToggleDisabledText), nameof(CanResetPassword))]
    private UserRow? _selectedUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(EditorTitle), nameof(SaveText), nameof(CanResetPassword), nameof(CanToggleDisabled))]
    private bool _isNew;

    [ObservableProperty] private string? _editEmail;
    [ObservableProperty] private string? _editName;
    [ObservableProperty] private bool _editIsAdmin;

    public ObservableCollection<UserRow> Users { get; } = [];

    public bool HasSelection => SelectedUser is not null;
    public bool IsEditing => IsNew || SelectedUser is not null;
    public string EditorTitle => IsNew ? Texts.Admin_NewUser : SelectedUser?.Name ?? "";
    public string SaveText => IsNew ? Texts.Admin_CreateUser : Texts.Admin_SaveChanges;
    public bool CanResetPassword => !IsNew && SelectedUser is not null;
    public bool CanToggleDisabled => !IsNew && SelectedUser is not null && SelectedUser.User.Id != myUserId;
    public string ToggleDisabledText => SelectedUser?.User.IsDisabled == true ? Texts.Admin_Enable : Texts.Admin_Disable;

    // ---------- Workspaces ----------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReassignOwnerCommand))]
    private AdminWorkspaceRow? _selectedWorkspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReassignOwnerCommand))]
    private UserRow? _newOwner;

    public ObservableCollection<AdminWorkspaceRow> Workspaces { get; } = [];

    /// <summary>Candidatos a Owner: los usuarios habilitados.</summary>
    public ObservableCollection<UserRow> OwnerCandidates { get; } = [];

    /// <summary>Hubo cambios que pueden afectar a los workspaces del propio usuario.</summary>
    public bool Changed { get; private set; }

    public Task LoadAsync() => GuardAsync(async () =>
    {
        await LoadUsersAsync();
        await LoadWorkspacesAsync();
    });

    private async Task LoadUsersAsync(Guid? select = null)
    {
        _allUsers = (await api.GetUsersAsync()).Select(u => new UserRow(u)).ToList();
        ApplyFilter(select ?? SelectedUser?.User.Id);

        OwnerCandidates.Clear();
        foreach (var u in _allUsers.Where(u => !u.User.IsDisabled)) OwnerCandidates.Add(u);
    }

    private async Task LoadWorkspacesAsync()
    {
        var selected = SelectedWorkspace?.Workspace.Id;
        Workspaces.Clear();
        foreach (var w in await api.GetAllWorkspacesAsync()) Workspaces.Add(new AdminWorkspaceRow(w));
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Workspace.Id == selected);
    }

    partial void OnSearchChanged(string? value) => ApplyFilter(SelectedUser?.User.Id);

    private void ApplyFilter(Guid? select)
    {
        Users.Clear();
        foreach (var u in _allUsers.Where(u => u.Matches(Search))) Users.Add(u);
        SelectedUser = Users.FirstOrDefault(u => u.User.Id == select);
    }

    partial void OnSelectedUserChanged(UserRow? value)
    {
        if (value is null) return;
        IsNew = false;
        EditEmail = value.User.Email;
        EditName = value.User.DisplayName;
        EditIsAdmin = value.User.IsAdmin;
    }

    [RelayCommand]
    private void NewUser()
    {
        SelectedUser = null;
        IsNew = true;
        EditEmail = "";
        EditName = "";
        EditIsAdmin = false;
        Error = null;
    }

    [RelayCommand]
    private Task SaveUserAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(EditEmail) || string.IsNullOrWhiteSpace(EditName))
        {
            Error = Texts.Admin_MissingFields;
            return;
        }

        if (IsNew)
        {
            var created = await api.CreateUserAsync(new CreateUserRequest(EditEmail.Trim(), EditName.Trim(), EditIsAdmin));
            await LoadUsersAsync(created.User.Id);
            await dialogs.ShowSecretAsync(
                Texts.Admin_CreatedTitle,
                string.Format(Texts.Admin_CreatedMessage, created.User.DisplayName),
                created.TemporaryPassword);
            return;
        }

        var user = SelectedUser!.User;
        await api.UpdateUserAsync(user.Id, new UpdateUserRequest(EditEmail.Trim(), EditName.Trim(), EditIsAdmin));
        await LoadUsersAsync(user.Id);
        await LoadWorkspacesAsync(); // el nombre del Owner puede haber cambiado
    });

    [RelayCommand]
    private void CancelEdit()
    {
        if (IsNew)
        {
            IsNew = false;
            return;
        }
        OnSelectedUserChanged(SelectedUser);
    }

    [RelayCommand]
    private Task ResetPasswordAsync() => GuardAsync(async () =>
    {
        var user = SelectedUser!.User;
        if (!await dialogs.ConfirmAsync(Texts.Admin_ResetTitle,
                string.Format(Texts.Admin_ResetMessage, user.DisplayName), Texts.Admin_Reset))
            return;

        var temporary = await api.ResetPasswordAsync(user.Id);
        await LoadUsersAsync(user.Id);
        await dialogs.ShowSecretAsync(
            Texts.Admin_ResetDoneTitle,
            string.Format(Texts.Admin_ResetDoneMessage, user.DisplayName),
            temporary);
    });

    [RelayCommand]
    private Task ToggleDisabledAsync() => GuardAsync(async () =>
    {
        var user = SelectedUser!.User;
        if (user.IsDisabled)
        {
            await api.EnableUserAsync(user.Id);
        }
        else
        {
            if (!await dialogs.ConfirmAsync(Texts.Admin_DisableTitle,
                    string.Format(Texts.Admin_DisableMessage, user.DisplayName), Texts.Admin_Disable))
                return;
            await api.DisableUserAsync(user.Id);
        }
        await LoadUsersAsync(user.Id);
    });

    // ---------- Workspaces ----------

    private bool CanReassign => SelectedWorkspace is not null && NewOwner is not null
        && NewOwner.User.Id != SelectedWorkspace.Workspace.Owner?.Id;

    [RelayCommand(CanExecute = nameof(CanReassign))]
    private Task ReassignOwnerAsync() => GuardAsync(async () =>
    {
        var ws = SelectedWorkspace!.Workspace;
        var owner = NewOwner!.User;
        if (!await dialogs.ConfirmAsync(Texts.Admin_ReassignTitle,
                string.Format(Texts.Admin_ReassignMessage, owner.DisplayName, ws.Name), Texts.Admin_Reassign))
            return;

        await api.SetOwnerAsync(ws.Id, owner.Id);
        Changed = true;
        NewOwner = null;
        await LoadWorkspacesAsync();
    });

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();
}
