using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Tests;

/// <summary>Lo justo de un store para copiar y mover: crear, listar y actualizar.</summary>
internal sealed class MemoryStore : IWorkspaceStore
{
    public List<AppDto> Apps { get; } = [];
    public List<FolderDto> Folders { get; } = [];
    public List<RequestDto> Requests { get; } = [];
    public List<SampleDto> Samples { get; } = [];
    public List<LoginDto> Logins { get; } = [];
    public List<VariableDto> Variables { get; } = [];

    /// <summary>Cuántas actualizaciones recibió: para ver que no se reescribe lo que no cambia.</summary>
    public int Updates { get; private set; }

    private Task<T> Replace<T>(List<T> list, Predicate<T> match, Func<T, T> change)
    {
        var index = list.FindIndex(match);
        list[index] = change(list[index]);
        Updates++;
        return Task.FromResult(list[index]);
    }

    private static T Add<T>(List<T> list, T item)
    {
        list.Add(item);
        return item;
    }

    public Task<AppDto> CreateAppAsync(Guid ws, CreateAppRequest req, CancellationToken ct = default) =>
        Task.FromResult(Add(Apps, new AppDto(Guid.NewGuid(), req.Name, req.Order, 1, 0, req.Auth, req.LoginId)));

    public Task<FolderDto> CreateFolderAsync(Guid ws, CreateFolderRequest req, CancellationToken ct = default) =>
        Task.FromResult(Add(Folders, new FolderDto(Guid.NewGuid(), req.AppId, req.ParentFolderId, req.Name, req.Order, 1, 0)));

    public Task<RequestDto> CreateRequestAsync(Guid ws, CreateRequestRequest req, CancellationToken ct = default) =>
        Task.FromResult(Add(Requests, new RequestDto(
            Guid.NewGuid(), req.AppId, req.FolderId, req.Name, req.Order, req.Spec ?? RequestSpec.Empty, 1, 0)));

    public Task<SampleDto> CreateSampleAsync(Guid ws, CreateSampleRequest req, CancellationToken ct = default) =>
        Task.FromResult(Add(Samples, new SampleDto(
            Guid.NewGuid(), req.RequestId, req.Name, req.Order, req.Overrides ?? SampleOverrides.Empty, 1, 0)));

    public Task<LoginDto> CreateLoginAsync(Guid ws, CreateLoginRequest req, CancellationToken ct = default) =>
        Task.FromResult(Add(Logins, new LoginDto(Guid.NewGuid(), req.Name, req.Config, 1, 0)));

    // Se devuelven copias: el duplicador no tiene que ver lo que crea mientras recorre.
    public Task<IReadOnlyList<AppDto>> GetAppsAsync(Guid ws, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppDto>>(Apps.ToList());
    public Task<IReadOnlyList<FolderDto>> GetFoldersAsync(Guid ws, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FolderDto>>(Folders.ToList());
    public Task<IReadOnlyList<RequestDto>> GetRequestsAsync(Guid ws, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RequestDto>>(Requests.ToList());
    public Task<IReadOnlyList<SampleDto>> GetSamplesAsync(Guid ws, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SampleDto>>(Samples.ToList());
    public Task<IReadOnlyList<LoginDto>> GetLoginsAsync(Guid ws, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoginDto>>(Logins.ToList());

    public Task<IReadOnlyList<WorkspaceDto>> GetWorkspacesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<WorkspaceDto> CreateWorkspaceAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<EnvironmentDto>> GetEnvironmentsAsync(Guid ws, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<EnvironmentDto> CreateEnvironmentAsync(Guid ws, string name, Guid? id = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<EnvironmentDto> UpdateEnvironmentAsync(Guid ws, Guid id, UpdateEnvironmentRequest req, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteEnvironmentAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<VariableDto>> GetVariablesAsync(Guid ws, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VariableDto>>(Variables.ToList());
    public Task<VariableDto> CreateVariableAsync(Guid ws, CreateVariableRequest req, CancellationToken ct = default) =>
        Task.FromResult(Add(Variables, new VariableDto(Guid.NewGuid(), req.EnvironmentId, req.Key, req.Value, req.IsSecret, 1, 0, req.AppId)));
    public Task<VariableDto> UpdateVariableAsync(Guid ws, Guid id, UpdateVariableRequest req, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteVariableAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AppDto> UpdateAppAsync(Guid ws, Guid id, UpdateAppRequest req, CancellationToken ct = default) =>
        Replace(Apps, a => a.Id == id, a => a with { Name = req.Name, Order = req.Order, Auth = req.Auth, LoginId = req.LoginId, Version = a.Version + 1 });
    public Task DeleteAppAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<FolderDto> UpdateFolderAsync(Guid ws, Guid id, UpdateFolderRequest req, CancellationToken ct = default) =>
        Replace(Folders, f => f.Id == id, f => f with { Name = req.Name, ParentFolderId = req.ParentFolderId, Order = req.Order, Version = f.Version + 1 });
    public Task DeleteFolderAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RequestDto> GetRequestAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RequestDto> UpdateRequestAsync(Guid ws, Guid id, UpdateRequestRequest req, CancellationToken ct = default) =>
        Replace(Requests, r => r.Id == id, r => r with { Name = req.Name, FolderId = req.FolderId, Order = req.Order, Spec = req.Spec, Version = r.Version + 1 });
    public Task DeleteRequestAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SampleDto> GetSampleAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SampleDto> UpdateSampleAsync(Guid ws, Guid id, UpdateSampleRequest req, CancellationToken ct = default) =>
        Replace(Samples, s => s.Id == id, s => s with { Name = req.Name, Order = req.Order, Overrides = req.Overrides, Version = s.Version + 1 });
    public Task DeleteSampleAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<LoginDto> UpdateLoginAsync(Guid ws, Guid id, UpdateLoginRequest req, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteLoginAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ProtoSetDto>> GetProtoSetsAsync(Guid ws, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ProtoSetDto> CreateProtoSetAsync(Guid ws, CreateProtoSetRequest req, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ProtoSetDto> UpdateProtoSetAsync(Guid ws, Guid id, UpdateProtoSetRequest req, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteProtoSetAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
}
