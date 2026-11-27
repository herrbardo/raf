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
        var env = Guid.NewGuid();
        await store.CreateVariableAsync(Ws, new CreateVariableRequest("baseUrl", "dev", env, AppId: app.Id));
        await store.CreateVariableAsync(Ws, new CreateVariableRequest("clave", "s", IsSecret: true, AppId: app.Id));
        await store.CreateVariableAsync(Ws, new CreateVariableRequest("deTodas", "x"));
        await store.CreateVariableAsync(Ws, new CreateVariableRequest("deOtra", "y", AppId: other.Id));

        var copy = await new WorkspaceDuplicator(store, Ws).DuplicateAppAsync(app, "App (1)", 1);

        Assert.Equal(("App (1)", 1, auth, (Guid?)login.Id), (copy.Name, copy.Order, copy.Auth, copy.LoginId));
        Assert.Equal([("baseUrl", "dev", (Guid?)env, false), ("clave", "s", null, true)],
            store.Variables.Where(v => v.AppId == copy.Id).Select(v => (v.Key, v.Value!, v.EnvironmentId, v.IsSecret)).OrderBy(v => v.Key));
        Assert.Equal(6, store.Variables.Count);
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
}
