using Raf.Client.Core.Api;
using Raf.Contracts;

namespace Raf.Client.Core.Workspaces;

/// <summary>
/// Acceso a los datos de los workspaces. Los ViewModels usan <see cref="Sync.SyncedWorkspaceStore"/>, que trabaja sobre
/// la caché local; <see cref="ApiWorkspaceStore"/> va directo al servidor y es lo que usa el motor de sincronización.
/// </summary>
public interface IWorkspaceStore
{
    Task<IReadOnlyList<WorkspaceDto>> GetWorkspacesAsync(CancellationToken ct = default);
    Task<WorkspaceDto> CreateWorkspaceAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<EnvironmentDto>> GetEnvironmentsAsync(Guid ws, CancellationToken ct = default);
    Task<EnvironmentDto> CreateEnvironmentAsync(Guid ws, string name, Guid? id = null, CancellationToken ct = default);
    Task<EnvironmentDto> UpdateEnvironmentAsync(Guid ws, Guid id, UpdateEnvironmentRequest req, CancellationToken ct = default);
    Task DeleteEnvironmentAsync(Guid ws, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<VariableDto>> GetVariablesAsync(Guid ws, CancellationToken ct = default);
    Task<VariableDto> CreateVariableAsync(Guid ws, CreateVariableRequest req, CancellationToken ct = default);
    Task<VariableDto> UpdateVariableAsync(Guid ws, Guid id, UpdateVariableRequest req, CancellationToken ct = default);
    Task DeleteVariableAsync(Guid ws, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<AppDto>> GetAppsAsync(Guid ws, CancellationToken ct = default);
    Task<AppDto> CreateAppAsync(Guid ws, CreateAppRequest req, CancellationToken ct = default);
    Task<AppDto> UpdateAppAsync(Guid ws, Guid id, UpdateAppRequest req, CancellationToken ct = default);
    Task DeleteAppAsync(Guid ws, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<FolderDto>> GetFoldersAsync(Guid ws, CancellationToken ct = default);
    Task<FolderDto> CreateFolderAsync(Guid ws, CreateFolderRequest req, CancellationToken ct = default);
    Task<FolderDto> UpdateFolderAsync(Guid ws, Guid id, UpdateFolderRequest req, CancellationToken ct = default);
    Task DeleteFolderAsync(Guid ws, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<RequestDto>> GetRequestsAsync(Guid ws, CancellationToken ct = default);
    Task<RequestDto> GetRequestAsync(Guid ws, Guid id, CancellationToken ct = default);
    Task<RequestDto> CreateRequestAsync(Guid ws, CreateRequestRequest req, CancellationToken ct = default);
    Task<RequestDto> UpdateRequestAsync(Guid ws, Guid id, UpdateRequestRequest req, CancellationToken ct = default);
    Task DeleteRequestAsync(Guid ws, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<SampleDto>> GetSamplesAsync(Guid ws, CancellationToken ct = default);
    Task<SampleDto> GetSampleAsync(Guid ws, Guid id, CancellationToken ct = default);
    Task<SampleDto> CreateSampleAsync(Guid ws, CreateSampleRequest req, CancellationToken ct = default);
    Task<SampleDto> UpdateSampleAsync(Guid ws, Guid id, UpdateSampleRequest req, CancellationToken ct = default);
    Task DeleteSampleAsync(Guid ws, Guid id, CancellationToken ct = default);
}

public sealed class ApiWorkspaceStore(RafApiClient api) : IWorkspaceStore
{
    private static string P(Guid ws, string kind) => $"workspaces/{ws}/{kind}";

    private static string P(Guid ws, string kind, Guid id) => $"workspaces/{ws}/{kind}/{id}";

    private async Task<IReadOnlyList<T>> ListAsync<T>(Guid ws, string kind, CancellationToken ct) =>
        await api.GetAsync<List<T>>(P(ws, kind), ct);

    public async Task<IReadOnlyList<WorkspaceDto>> GetWorkspacesAsync(CancellationToken ct = default) =>
        await api.GetAsync<List<WorkspaceDto>>("workspaces", ct);

    public Task<WorkspaceDto> CreateWorkspaceAsync(string name, CancellationToken ct = default) =>
        CreateWorkspaceAsync(name, Guid.CreateVersion7(), ct);

    public Task<WorkspaceDto> CreateWorkspaceAsync(string name, Guid id, CancellationToken ct) =>
        api.PostAsync<WorkspaceDto>("workspaces", new CreateWorkspaceRequest(name, id), ct);

    public Task<ChangesResponse> GetChangesAsync(Guid ws, long since, int limit, CancellationToken ct = default) =>
        api.GetAsync<ChangesResponse>($"workspaces/{ws}/changes?since={since}&limit={limit}", ct);

    public Task<IReadOnlyList<EnvironmentDto>> GetEnvironmentsAsync(Guid ws, CancellationToken ct = default) =>
        ListAsync<EnvironmentDto>(ws, "environments", ct);

    public Task<EnvironmentDto> CreateEnvironmentAsync(Guid ws, string name, Guid? id = null, CancellationToken ct = default) =>
        api.PostAsync<EnvironmentDto>(P(ws, "environments"), new CreateEnvironmentRequest(name, id ?? Guid.CreateVersion7()), ct);

    public Task<EnvironmentDto> UpdateEnvironmentAsync(Guid ws, Guid id, UpdateEnvironmentRequest req, CancellationToken ct = default) =>
        api.PutAsync<EnvironmentDto>(P(ws, "environments", id), req, ct);

    public Task DeleteEnvironmentAsync(Guid ws, Guid id, CancellationToken ct = default) =>
        api.DeleteAsync(P(ws, "environments", id), ct);

    public Task<IReadOnlyList<VariableDto>> GetVariablesAsync(Guid ws, CancellationToken ct = default) =>
        ListAsync<VariableDto>(ws, "variables", ct);

    public Task<VariableDto> CreateVariableAsync(Guid ws, CreateVariableRequest req, CancellationToken ct = default) =>
        api.PostAsync<VariableDto>(P(ws, "variables"), req with { Id = req.Id ?? Guid.CreateVersion7() }, ct);

    public Task<VariableDto> UpdateVariableAsync(Guid ws, Guid id, UpdateVariableRequest req, CancellationToken ct = default) =>
        api.PutAsync<VariableDto>(P(ws, "variables", id), req, ct);

    public Task DeleteVariableAsync(Guid ws, Guid id, CancellationToken ct = default) =>
        api.DeleteAsync(P(ws, "variables", id), ct);

    public Task<IReadOnlyList<AppDto>> GetAppsAsync(Guid ws, CancellationToken ct = default) => ListAsync<AppDto>(ws, "apps", ct);

    public Task<AppDto> CreateAppAsync(Guid ws, CreateAppRequest req, CancellationToken ct = default) =>
        api.PostAsync<AppDto>(P(ws, "apps"), req with { Id = req.Id ?? Guid.CreateVersion7() }, ct);

    public Task<AppDto> UpdateAppAsync(Guid ws, Guid id, UpdateAppRequest req, CancellationToken ct = default) =>
        api.PutAsync<AppDto>(P(ws, "apps", id), req, ct);

    public Task DeleteAppAsync(Guid ws, Guid id, CancellationToken ct = default) => api.DeleteAsync(P(ws, "apps", id), ct);

    public Task<IReadOnlyList<FolderDto>> GetFoldersAsync(Guid ws, CancellationToken ct = default) =>
        ListAsync<FolderDto>(ws, "folders", ct);

    public Task<FolderDto> CreateFolderAsync(Guid ws, CreateFolderRequest req, CancellationToken ct = default) =>
        api.PostAsync<FolderDto>(P(ws, "folders"), req with { Id = req.Id ?? Guid.CreateVersion7() }, ct);

    public Task<FolderDto> UpdateFolderAsync(Guid ws, Guid id, UpdateFolderRequest req, CancellationToken ct = default) =>
        api.PutAsync<FolderDto>(P(ws, "folders", id), req, ct);

    public Task DeleteFolderAsync(Guid ws, Guid id, CancellationToken ct = default) => api.DeleteAsync(P(ws, "folders", id), ct);

    public Task<IReadOnlyList<RequestDto>> GetRequestsAsync(Guid ws, CancellationToken ct = default) =>
        ListAsync<RequestDto>(ws, "requests", ct);

    public Task<RequestDto> GetRequestAsync(Guid ws, Guid id, CancellationToken ct = default) =>
        api.GetAsync<RequestDto>(P(ws, "requests", id), ct);

    public Task<RequestDto> CreateRequestAsync(Guid ws, CreateRequestRequest req, CancellationToken ct = default) =>
        api.PostAsync<RequestDto>(P(ws, "requests"), req with { Id = req.Id ?? Guid.CreateVersion7() }, ct);

    public Task<RequestDto> UpdateRequestAsync(Guid ws, Guid id, UpdateRequestRequest req, CancellationToken ct = default) =>
        api.PutAsync<RequestDto>(P(ws, "requests", id), req, ct);

    public Task DeleteRequestAsync(Guid ws, Guid id, CancellationToken ct = default) => api.DeleteAsync(P(ws, "requests", id), ct);

    public Task<IReadOnlyList<SampleDto>> GetSamplesAsync(Guid ws, CancellationToken ct = default) =>
        ListAsync<SampleDto>(ws, "samples", ct);

    public Task<SampleDto> GetSampleAsync(Guid ws, Guid id, CancellationToken ct = default) =>
        api.GetAsync<SampleDto>(P(ws, "samples", id), ct);

    public Task<SampleDto> CreateSampleAsync(Guid ws, CreateSampleRequest req, CancellationToken ct = default) =>
        api.PostAsync<SampleDto>(P(ws, "samples"), req with { Id = req.Id ?? Guid.CreateVersion7() }, ct);

    public Task<SampleDto> UpdateSampleAsync(Guid ws, Guid id, UpdateSampleRequest req, CancellationToken ct = default) =>
        api.PutAsync<SampleDto>(P(ws, "samples", id), req, ct);

    public Task DeleteSampleAsync(Guid ws, Guid id, CancellationToken ct = default) => api.DeleteAsync(P(ws, "samples", id), ct);
}
