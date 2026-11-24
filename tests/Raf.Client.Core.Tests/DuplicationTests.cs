using Raf.Client.Core.Workspaces;
using Raf.Contracts;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Tests;

public class DuplicationTests
{
    [Theory]
    [InlineData("request", new string[0], "request (1)")]
    [InlineData("request", new[] { "request", "request (1)" }, "request (2)")]
    [InlineData("request", new[] { "REQUEST (1)", "request (3)" }, "request (2)")]
    [InlineData("request (1)", new[] { "request", "request (1)" }, "request (2)")]
    [InlineData("v (2) final", new[] { "v (2) final" }, "v (2) final (1)")]
    [InlineData("(1)", new[] { "(1)" }, "(1) (1)")]
    public void Names_the_copy_with_the_first_free_number(string name, string[] siblings, string expected) =>
        Assert.Equal(expected, DuplicateName.Next(name, siblings));

    [Fact]
    public async Task Duplicates_a_request_with_its_samples()
    {
        var store = new MemoryStore();
        var app = await store.CreateAppAsync(Ws, new CreateAppRequest("App"));
        var spec = RequestSpec.Empty with { Method = "POST", Url = "{{base}}/users" };
        var request = await store.CreateRequestAsync(Ws, new CreateRequestRequest(app.Id, "Alta", spec, null, 3));
        var overrides = SampleOverrides.Empty with { Url = "{{base}}/otro" };
        await store.CreateSampleAsync(Ws, new CreateSampleRequest(request.Id, "Caso", overrides, 2));

        var copy = await new WorkspaceDuplicator(store, Ws).DuplicateRequestAsync(request, "Alta (1)", 4);

        Assert.NotEqual(request.Id, copy.Id);
        Assert.Equal((app.Id, (Guid?)null, "Alta (1)", 4, spec), (copy.AppId, copy.FolderId, copy.Name, copy.Order, copy.Spec));
        var sample = Assert.Single(store.Samples, s => s.RequestId == copy.Id);
        Assert.Equal(("Caso", 2, overrides), (sample.Name, sample.Order, sample.Overrides));
        Assert.Equal(2, store.Samples.Count);
    }

    [Fact]
    public async Task Duplicates_a_folder_with_everything_inside_and_nothing_else()
    {
        var store = new MemoryStore();
        var app = await store.CreateAppAsync(Ws, new CreateAppRequest("App"));
        var parent = await store.CreateFolderAsync(Ws, new CreateFolderRequest(app.Id, "Padre"));
        var folder = await store.CreateFolderAsync(Ws, new CreateFolderRequest(app.Id, "Usuarios", parent.Id, 1));
        var sub = await store.CreateFolderAsync(Ws, new CreateFolderRequest(app.Id, "Sub", folder.Id, 5));
        var inSub = await store.CreateRequestAsync(Ws, new CreateRequestRequest(app.Id, "Profundo", null, sub.Id, 7));
        await store.CreateSampleAsync(Ws, new CreateSampleRequest(inSub.Id, "Caso"));
        await store.CreateRequestAsync(Ws, new CreateRequestRequest(app.Id, "Listar", null, folder.Id));
        await store.CreateRequestAsync(Ws, new CreateRequestRequest(app.Id, "Afuera", null, parent.Id)); // hermano: no se copia

        var copy = await new WorkspaceDuplicator(store, Ws).DuplicateFolderAsync(folder, "Usuarios (1)", 2);

        Assert.Equal((app.Id, (Guid?)parent.Id, "Usuarios (1)", 2), (copy.AppId, copy.ParentFolderId, copy.Name, copy.Order));
        var subCopy = Assert.Single(store.Folders, f => f.ParentFolderId == copy.Id);
        Assert.Equal(("Sub", 5), (subCopy.Name, subCopy.Order));
        Assert.Equal("Listar", Assert.Single(store.Requests, r => r.FolderId == copy.Id).Name);
        var deep = Assert.Single(store.Requests, r => r.FolderId == subCopy.Id);
        Assert.Equal(("Profundo", 7), (deep.Name, deep.Order));
        Assert.Single(store.Samples, s => s.RequestId == deep.Id);
        Assert.Equal(5, store.Folders.Count);
        Assert.Equal(5, store.Requests.Count);
    }

    [Fact]
    public async Task Duplicates_an_app_with_its_auth_login_and_contents()
    {
        var store = new MemoryStore();
        var login = await store.CreateLoginAsync(Ws, new CreateLoginRequest("Login", AppLogin.Default));
        var auth = new AuthSpec(AuthType.Bearer, "{{token}}");
        var app = await store.CreateAppAsync(Ws, new CreateAppRequest("App", 0, Auth: auth, LoginId: login.Id));
        var other = await store.CreateAppAsync(Ws, new CreateAppRequest("Otra", 1));
        await store.CreateRequestAsync(Ws, new CreateRequestRequest(other.Id, "De otra App"));
        var folder = await store.CreateFolderAsync(Ws, new CreateFolderRequest(app.Id, "Carpeta"));
        await store.CreateRequestAsync(Ws, new CreateRequestRequest(app.Id, "En carpeta", null, folder.Id));
        await store.CreateRequestAsync(Ws, new CreateRequestRequest(app.Id, "En la raíz"));

        var copy = await new WorkspaceDuplicator(store, Ws).DuplicateAppAsync(app, "App (1)", 1);

        Assert.Equal(("App (1)", 1, auth, (Guid?)login.Id), (copy.Name, copy.Order, copy.Auth, copy.LoginId));
        var folderCopy = Assert.Single(store.Folders, f => f.AppId == copy.Id);
        Assert.Equal("En carpeta", Assert.Single(store.Requests, r => r.FolderId == folderCopy.Id).Name);
        Assert.Equal(["En carpeta", "En la raíz"], store.Requests.Where(r => r.AppId == copy.Id).Select(r => r.Name).Order());
    }

    [Fact]
    public async Task Duplicates_a_sample_and_a_login()
    {
        var store = new MemoryStore();
        var app = await store.CreateAppAsync(Ws, new CreateAppRequest("App"));
        var request = await store.CreateRequestAsync(Ws, new CreateRequestRequest(app.Id, "R"));
        var overrides = SampleOverrides.Empty with { Url = "x" };
        var sample = await store.CreateSampleAsync(Ws, new CreateSampleRequest(request.Id, "S", overrides));
        var config = AppLogin.Default with { Enabled = true };
        var login = await store.CreateLoginAsync(Ws, new CreateLoginRequest("L", config));
        var duplicator = new WorkspaceDuplicator(store, Ws);

        var sampleCopy = await duplicator.DuplicateSampleAsync(sample, "S (1)", 1);
        var loginCopy = await duplicator.DuplicateLoginAsync(login, "L (1)");

        Assert.Equal((request.Id, "S (1)", 1, overrides), (sampleCopy.RequestId, sampleCopy.Name, sampleCopy.Order, sampleCopy.Overrides));
        Assert.Equal(("L (1)", config), (loginCopy.Name, loginCopy.Config));
        Assert.NotEqual(login.Id, loginCopy.Id);
    }

    private static readonly Guid Ws = Guid.NewGuid();

    /// <summary>Lo justo de un store para copiar: crear y listar.</summary>
    private sealed class MemoryStore : IWorkspaceStore
    {
        public List<AppDto> Apps { get; } = [];
        public List<FolderDto> Folders { get; } = [];
        public List<RequestDto> Requests { get; } = [];
        public List<SampleDto> Samples { get; } = [];
        public List<LoginDto> Logins { get; } = [];

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
        public Task<IReadOnlyList<VariableDto>> GetVariablesAsync(Guid ws, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<VariableDto> CreateVariableAsync(Guid ws, CreateVariableRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<VariableDto> UpdateVariableAsync(Guid ws, Guid id, UpdateVariableRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteVariableAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AppDto> UpdateAppAsync(Guid ws, Guid id, UpdateAppRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAppAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FolderDto> UpdateFolderAsync(Guid ws, Guid id, UpdateFolderRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFolderAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RequestDto> GetRequestAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RequestDto> UpdateRequestAsync(Guid ws, Guid id, UpdateRequestRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRequestAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SampleDto> GetSampleAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SampleDto> UpdateSampleAsync(Guid ws, Guid id, UpdateSampleRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSampleAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LoginDto> UpdateLoginAsync(Guid ws, Guid id, UpdateLoginRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteLoginAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProtoSetDto>> GetProtoSetsAsync(Guid ws, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProtoSetDto> CreateProtoSetAsync(Guid ws, CreateProtoSetRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProtoSetDto> UpdateProtoSetAsync(Guid ws, Guid id, UpdateProtoSetRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProtoSetAsync(Guid ws, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
