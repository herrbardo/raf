using Raf.Domain.Entities;
using Raf.Domain.Requests;

namespace Raf.Contracts;

// ---------- Auth ----------

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto User);

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsAdmin,
    bool IsDisabled,
    bool MustChangePassword);

/// <summary>Datos públicos de un usuario, para buscar y agregar miembros.</summary>
public sealed record UserSummaryDto(Guid Id, string Email, string DisplayName);

// ---------- Admin ----------

public sealed record CreateUserRequest(string Email, string DisplayName, bool IsAdmin = false);

public sealed record CreateUserResponse(UserDto User, string TemporaryPassword);

public sealed record UpdateUserRequest(string Email, string DisplayName, bool IsAdmin);

public sealed record ResetPasswordResponse(string TemporaryPassword);

public sealed record AdminWorkspaceDto(Guid Id, string Name, UserSummaryDto? Owner, int MemberCount);

public sealed record SetOwnerRequest(Guid UserId);

// ---------- Workspaces ----------

public sealed record WorkspaceDto(Guid Id, string Name, WorkspaceRole MyRole, long Version, long ChangeSeq);

public sealed record CreateWorkspaceRequest(string Name, Guid? Id = null);

public sealed record UpdateWorkspaceRequest(string Name, long Version);

public sealed record MemberDto(
    Guid UserId, string Email, string DisplayName, WorkspaceRole Role, long Version = 0, long ChangeSeq = 0);

/// <summary>Rol Editor o Viewer. La propiedad se transfiere con <see cref="TransferOwnershipRequest"/>.</summary>
public sealed record AddMemberRequest(Guid UserId, WorkspaceRole Role);

public sealed record UpdateMemberRequest(WorkspaceRole Role);

public sealed record TransferOwnershipRequest(Guid UserId);

// ---------- Environments y variables ----------

public sealed record EnvironmentDto(Guid Id, string Name, long Version, long ChangeSeq);

public sealed record CreateEnvironmentRequest(string Name, Guid? Id = null);

public sealed record UpdateEnvironmentRequest(string Name, long Version);

/// <summary><see cref="Value"/> es null cuando la variable es secreta y el usuario es Viewer.</summary>
public sealed record VariableDto(
    Guid Id, Guid? EnvironmentId, string Key, string? Value, bool IsSecret, long Version, long ChangeSeq);

/// <summary><see cref="EnvironmentId"/> null = variable global del workspace.</summary>
public sealed record CreateVariableRequest(
    string Key, string Value, Guid? EnvironmentId = null, bool IsSecret = false, Guid? Id = null);

public sealed record UpdateVariableRequest(string Key, string Value, bool IsSecret, long Version);

// ---------- Apps, carpetas, requests, samples ----------

/// <summary>
/// <see cref="Auth"/> null solo en datos de una versión anterior: equivale a sin autenticación.
/// <see cref="Login"/> null: la App nunca tuvo login automático.
/// </summary>
public sealed record AppDto(Guid Id, string Name, int Order, long Version, long ChangeSeq, AuthSpec? Auth = null, AppLogin? Login = null);

public sealed record CreateAppRequest(string Name, int Order = 0, Guid? Id = null, AuthSpec? Auth = null, AppLogin? Login = null);

/// <summary>
/// <see cref="Auth"/> o <see cref="Login"/> en null = no cambian. El login se apaga con
/// <see cref="AppLogin.Enabled"/> en false, sin perder la configuración.
/// </summary>
public sealed record UpdateAppRequest(string Name, int Order, long Version, AuthSpec? Auth = null, AppLogin? Login = null);

public sealed record FolderDto(
    Guid Id, Guid AppId, Guid? ParentFolderId, string Name, int Order, long Version, long ChangeSeq);

public sealed record CreateFolderRequest(
    Guid AppId, string Name, Guid? ParentFolderId = null, int Order = 0, Guid? Id = null);

public sealed record UpdateFolderRequest(string Name, Guid? ParentFolderId, int Order, long Version);

public sealed record RequestDto(
    Guid Id, Guid AppId, Guid? FolderId, string Name, int Order, RequestSpec Spec, long Version, long ChangeSeq);

public sealed record CreateRequestRequest(
    Guid AppId, string Name, RequestSpec? Spec = null, Guid? FolderId = null, int Order = 0, Guid? Id = null);

public sealed record UpdateRequestRequest(string Name, Guid? FolderId, int Order, RequestSpec Spec, long Version);

public sealed record SampleDto(
    Guid Id, Guid RequestId, string Name, int Order, SampleOverrides Overrides, long Version, long ChangeSeq);

public sealed record CreateSampleRequest(
    Guid RequestId, string Name, SampleOverrides? Overrides = null, int Order = 0, Guid? Id = null);

public sealed record UpdateSampleRequest(string Name, int Order, SampleOverrides Overrides, long Version);

// ---------- Sincronización ----------

public enum ChangeKind { Workspace, Member, Environment, Variable, App, Folder, Request, Sample }

/// <summary>Elemento borrado. En miembros, <see cref="Id"/> es el id del usuario.</summary>
public sealed record DeletedItem(ChangeKind Kind, Guid Id, long ChangeSeq);

/// <summary>
/// Cambios posteriores a un <c>since</c>. Para seguir, se pide <c>since = LastSeq</c>. Un mismo número de cambio
/// nunca se parte entre páginas, así que una página puede superar levemente el límite pedido.
/// </summary>
public sealed record ChangesResponse(
    long LastSeq,
    bool HasMore,
    WorkspaceRole MyRole,
    WorkspaceDto? Workspace,
    IReadOnlyList<MemberDto> Members,
    IReadOnlyList<EnvironmentDto> Environments,
    IReadOnlyList<VariableDto> Variables,
    IReadOnlyList<AppDto> Apps,
    IReadOnlyList<FolderDto> Folders,
    IReadOnlyList<RequestDto> Requests,
    IReadOnlyList<SampleDto> Samples,
    IReadOnlyList<DeletedItem> Deleted);
