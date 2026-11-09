using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raf.Contracts;
using Raf.Domain.Entities;
using Raf.Server.Confidential;
using Raf.Server.Data;

namespace Raf.Server.Tests;

public class ConfidentialTests(RafApp app)
{
    private async Task<(HttpClient Owner, WorkspaceDto Ws)> NewWorkspaceAsync()
    {
        var (_, owner) = await app.CreateActiveUserAsync();
        var ws = await owner.PostOkAsync<WorkspaceDto>("/workspaces", new CreateWorkspaceRequest("Secretos"));
        return (owner, ws);
    }

    /// <summary>Lo que quedó guardado en la columna, sin pasar por la API.</summary>
    private async Task<Variable> StoredAsync(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RafDbContext>();
        return await db.Variables.AsNoTracking().FirstAsync(v => v.Id == id);
    }

    [Fact]
    public async Task Secrets_are_stored_encrypted_and_read_in_clear_by_editors()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var created = await owner.PostOkAsync<VariableDto>(
            $"/workspaces/{ws.Id}/variables", new CreateVariableRequest("token", "s3cr3t-valor", IsSecret: true));
        Assert.Equal("s3cr3t-valor", created.Value);

        var stored = await StoredAsync(created.Id);
        Assert.StartsWith(EnvelopeConfidentialManager.Prefix, stored.Value);
        Assert.DoesNotContain("s3cr3t-valor", stored.Value);

        var listed = await owner.GetOkAsync<List<VariableDto>>($"/workspaces/{ws.Id}/variables");
        Assert.Equal("s3cr3t-valor", Assert.Single(listed).Value);

        var changes = await owner.GetOkAsync<ChangesResponse>($"/workspaces/{ws.Id}/changes?since=0");
        Assert.Equal("s3cr3t-valor", Assert.Single(changes.Variables).Value);
    }

    [Fact]
    public async Task Same_value_encrypts_differently_each_time()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var a = await owner.PostOkAsync<VariableDto>($"/workspaces/{ws.Id}/variables", new CreateVariableRequest("a", "igual", IsSecret: true));
        var b = await owner.PostOkAsync<VariableDto>($"/workspaces/{ws.Id}/variables", new CreateVariableRequest("b", "igual", IsSecret: true));
        Assert.NotEqual((await StoredAsync(a.Id)).Value, (await StoredAsync(b.Id)).Value);
    }

    [Fact]
    public async Task Changing_isSecret_encrypts_or_decrypts_the_stored_value()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var v = await owner.PostOkAsync<VariableDto>($"/workspaces/{ws.Id}/variables", new CreateVariableRequest("url", "https://x"));
        Assert.Equal("https://x", (await StoredAsync(v.Id)).Value);

        var res = await owner.PutJsonAsync($"/workspaces/{ws.Id}/variables/{v.Id}", new UpdateVariableRequest("url", "https://x", true, v.Version));
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        var secret = (await StoredAsync(v.Id));
        Assert.StartsWith(EnvelopeConfidentialManager.Prefix, secret.Value);

        res = await owner.PutJsonAsync($"/workspaces/{ws.Id}/variables/{v.Id}", new UpdateVariableRequest("url", "https://x", false, secret.Version));
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal("https://x", (await StoredAsync(v.Id)).Value);
    }

    [Fact]
    public async Task Legacy_plaintext_secrets_are_encrypted_by_the_migration_and_keep_working()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        var v = await owner.PostOkAsync<VariableDto>($"/workspaces/{ws.Id}/variables", new CreateVariableRequest("token", "viejo", IsSecret: true));

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RafDbContext>();
        // Simula un dato anterior al cifrado: el valor en claro en la columna.
        await db.Variables.Where(x => x.Id == v.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, "viejo"));
        var before = await StoredAsync(v.Id);

        // Mientras no se migre, se sigue leyendo bien.
        Assert.Equal("viejo", Assert.Single(await owner.GetOkAsync<List<VariableDto>>($"/workspaces/{ws.Id}/variables")).Value);

        var confidential = scope.ServiceProvider.GetRequiredService<IConfidentialManager>();
        Assert.True(await ConfidentialSetup.EncryptLegacySecretsAsync(db, confidential) >= 1);
        Assert.Equal(0, await db.Variables.CountAsync(x => x.Id == v.Id && !x.Value.StartsWith(EnvelopeConfidentialManager.Prefix)));

        var after = await StoredAsync(v.Id);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ChangeSeq, after.ChangeSeq);
        Assert.Equal("viejo", Assert.Single(await owner.GetOkAsync<List<VariableDto>>($"/workspaces/{ws.Id}/variables")).Value);
    }

    [Fact]
    public async Task A_value_encrypted_for_one_workspace_cannot_be_read_in_another()
    {
        var (_, wsA) = await NewWorkspaceAsync();
        var (_, wsB) = await NewWorkspaceAsync();
        var confidential = app.Services.GetRequiredService<IConfidentialManager>();

        var encrypted = await confidential.ProtectAsync(wsA.Id, "solo-A");
        Assert.Equal("solo-A", await confidential.UnprotectAsync(wsA.Id, encrypted));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => confidential.UnprotectAsync(wsB.Id, encrypted));
    }

    [Fact]
    public async Task Each_workspace_gets_a_single_wrapped_key_even_with_concurrent_writes()
    {
        var (owner, ws) = await NewWorkspaceAsync();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => owner.PostOkAsync<VariableDto>(
            $"/workspaces/{ws.Id}/variables", new CreateVariableRequest($"k{i}", $"v{i}", IsSecret: true))));

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RafDbContext>();
        var key = await db.WorkspaceKeys.AsNoTracking().SingleAsync(k => k.WorkspaceId == ws.Id);
        Assert.Equal(ConfidentialOptions.LocalProvider, key.Provider);

        var values = await owner.GetOkAsync<List<VariableDto>>($"/workspaces/{ws.Id}/variables");
        Assert.All(values, v => Assert.Equal("v" + v.Key[1..], v.Value));
    }
}
