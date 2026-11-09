using Raf.Client.Core.Api;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Client.Core.Workspaces;

/// <summary>
/// Gestión del servidor (Admin) y de los workspaces (Owner). A diferencia del contenido, estas operaciones no se
/// encolan sin conexión: van directo al servidor y la sincronización trae el resultado.
/// </summary>
public sealed class ManagementApi(RafApiClient api)
{
    // ---------- Usuarios (Admin) ----------

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) =>
        await api.GetAsync<List<UserDto>>("admin/users", ct);

    public Task<CreateUserResponse> CreateUserAsync(CreateUserRequest req, CancellationToken ct = default) =>
        api.PostAsync<CreateUserResponse>("admin/users", req, ct);

    public Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest req, CancellationToken ct = default) =>
        api.PutAsync<UserDto>($"admin/users/{id}", req, ct);

    /// <summary>Devuelve la contraseña temporal nueva. El servidor no la vuelve a mostrar.</summary>
    public async Task<string> ResetPasswordAsync(Guid id, CancellationToken ct = default) =>
        (await api.PostAsync<ResetPasswordResponse>($"admin/users/{id}/reset-password", new { }, ct)).TemporaryPassword;

    public Task<UserDto> DisableUserAsync(Guid id, CancellationToken ct = default) =>
        api.PostAsync<UserDto>($"admin/users/{id}/disable", new { }, ct);

    public Task<UserDto> EnableUserAsync(Guid id, CancellationToken ct = default) =>
        api.PostAsync<UserDto>($"admin/users/{id}/enable", new { }, ct);

    // ---------- Workspaces (Admin) ----------

    public async Task<IReadOnlyList<AdminWorkspaceDto>> GetAllWorkspacesAsync(CancellationToken ct = default) =>
        await api.GetAsync<List<AdminWorkspaceDto>>("admin/workspaces", ct);

    public Task SetOwnerAsync(Guid ws, Guid userId, CancellationToken ct = default) =>
        api.ExecuteAsync(HttpMethod.Put, $"admin/workspaces/{ws}/owner", new SetOwnerRequest(userId), ct);

    // ---------- Workspace (miembros) ----------

    public Task<WorkspaceDto> GetWorkspaceAsync(Guid ws, CancellationToken ct = default) =>
        api.GetAsync<WorkspaceDto>($"workspaces/{ws}", ct);

    public Task<WorkspaceDto> RenameWorkspaceAsync(Guid ws, string name, long version, CancellationToken ct = default) =>
        api.PutAsync<WorkspaceDto>($"workspaces/{ws}", new UpdateWorkspaceRequest(name, version), ct);

    public Task DeleteWorkspaceAsync(Guid ws, CancellationToken ct = default) => api.DeleteAsync($"workspaces/{ws}", ct);

    public async Task<IReadOnlyList<MemberDto>> GetMembersAsync(Guid ws, CancellationToken ct = default) =>
        await api.GetAsync<List<MemberDto>>($"workspaces/{ws}/members", ct);

    /// <summary>Usuarios habilitados cuyo email o nombre contiene <paramref name="text"/> (hasta 20).</summary>
    public async Task<IReadOnlyList<UserSummaryDto>> SearchUsersAsync(string? text, CancellationToken ct = default) =>
        await api.GetAsync<List<UserSummaryDto>>($"users?search={Uri.EscapeDataString(text?.Trim() ?? "")}", ct);

    public Task<MemberDto> AddMemberAsync(Guid ws, Guid userId, WorkspaceRole role, CancellationToken ct = default) =>
        api.PostAsync<MemberDto>($"workspaces/{ws}/members", new AddMemberRequest(userId, role), ct);

    public Task ChangeRoleAsync(Guid ws, Guid userId, WorkspaceRole role, CancellationToken ct = default) =>
        api.ExecuteAsync(HttpMethod.Put, $"workspaces/{ws}/members/{userId}", new UpdateMemberRequest(role), ct);

    /// <summary>Con el id propio, es salir del workspace.</summary>
    public Task RemoveMemberAsync(Guid ws, Guid userId, CancellationToken ct = default) =>
        api.DeleteAsync($"workspaces/{ws}/members/{userId}", ct);

    public Task TransferOwnershipAsync(Guid ws, Guid userId, CancellationToken ct = default) =>
        api.ExecuteAsync(HttpMethod.Post, $"workspaces/{ws}/transfer", new TransferOwnershipRequest(userId), ct);
}
