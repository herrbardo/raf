using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Client.Core.Workspaces;

/// <summary>
/// Qué puede hacer el usuario según su rol en el workspace y si es Admin del servidor. La UI oculta o deshabilita
/// cada acción con esto; el servidor valida siempre por su cuenta. <see cref="Role"/> null = no es miembro.
/// </summary>
public sealed record WorkspacePermissions(WorkspaceRole? Role, bool IsAdmin)
{
    public static WorkspacePermissions For(WorkspaceDto? workspace, bool isAdmin) => new(workspace?.MyRole, isAdmin);

    public bool IsMember => Role is not null;

    /// <summary>Ver y ejecutar apps, requests y samples.</summary>
    public bool CanRun => IsMember;

    /// <summary>Crear, editar y borrar apps, carpetas, requests y samples.</summary>
    public bool CanEditContent => Role >= WorkspaceRole.Editor;

    public bool CanManageVariables => Role >= WorkspaceRole.Editor;

    public bool CanSeeSecrets => Role >= WorkspaceRole.Editor;

    public bool CanManageMembers => Role == WorkspaceRole.Owner;

    public bool CanRenameWorkspace => Role == WorkspaceRole.Owner;

    public bool CanDeleteWorkspace => Role == WorkspaceRole.Owner;

    public bool CanTransferOwnership => Role == WorkspaceRole.Owner;

    /// <summary>El Owner no puede salir: primero transfiere la propiedad.</summary>
    public bool CanLeave => Role is WorkspaceRole.Editor or WorkspaceRole.Viewer;

    /// <summary>Gestión de usuarios y reasignación de owners de cualquier workspace.</summary>
    public bool CanAdministerServer => IsAdmin;

    /// <summary>El rol del Owner solo cambia al transferir la propiedad.</summary>
    public bool CanChangeRoleOf(MemberDto member) => CanManageMembers && member.Role != WorkspaceRole.Owner;

    public bool CanRemove(MemberDto member) => CanManageMembers && member.Role != WorkspaceRole.Owner;

    public bool CanTransferTo(MemberDto member) => CanTransferOwnership && member.Role != WorkspaceRole.Owner;

    public string RoleText => Role switch
    {
        WorkspaceRole.Owner => CoreTexts.Role_Owner,
        WorkspaceRole.Editor => CoreTexts.Role_Editor,
        WorkspaceRole.Viewer => CoreTexts.Role_Viewer,
        _ => "",
    };
}
