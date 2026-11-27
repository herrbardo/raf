using System.Text.RegularExpressions;
using Raf.Contracts;

namespace Raf.Client.Core.Workspaces;

/// <summary>Nombres de las copias: "nombre (1)", "nombre (2)"...</summary>
public static partial class DuplicateName
{
    [GeneratedRegex(@"^(?<base>.*) \((?<n>\d+)\)$")]
    private static partial Regex Numbered();

    /// <summary>
    /// El primer "nombre (n)" que no usa ningún hermano. Si el original ya es una copia ("nombre (1)"), se numera a
    /// partir de su base: la copia de "nombre (1)" es "nombre (2)", no "nombre (1) (1)".
    /// </summary>
    public static string Next(string name, IEnumerable<string> siblings)
    {
        var used = siblings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = Numbered().Match(name) is { Success: true } m ? m.Groups["base"].Value : name;
        for (var n = 1; ; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>
/// Copia Apps, carpetas, requests, samples y logins con todo su contenido, a partir de lo guardado (no de lo que
/// esté sin guardar en una pestaña). Cada elemento copiado es uno nuevo, con su propio id.
/// </summary>
public sealed class WorkspaceDuplicator(IWorkspaceStore store, Guid ws)
{
    /// <summary>La App con su autenticación, su login, sus variables (Plan 11) y todo su contenido.</summary>
    public async Task<AppDto> DuplicateAppAsync(AppDto source, string name, int order, CancellationToken ct = default)
    {
        var tree = await LoadAsync(ct);
        var variables = (await store.GetVariablesAsync(ws, ct)).Where(v => v.AppId == source.Id).ToList();
        var copy = await store.CreateAppAsync(ws, new CreateAppRequest(name, order, Auth: source.Auth, LoginId: source.LoginId), ct);
        await CopyContentsAsync(tree, source.Id, null, copy.Id, null, ct);
        foreach (var v in variables)
            await store.CreateVariableAsync(ws, new CreateVariableRequest(v.Key, v.Value ?? "", v.EnvironmentId, v.IsSecret, AppId: copy.Id), ct);
        return copy;
    }

    /// <summary>La carpeta, en el mismo lugar, con sus subcarpetas, requests y samples.</summary>
    public Task<FolderDto> DuplicateFolderAsync(FolderDto source, string name, int order, CancellationToken ct = default) =>
        CopyFolderToAsync(source, name, order, source.AppId, source.ParentFolderId, ct);

    /// <summary>La carpeta con todo su contenido, dentro de otra carpeta o App (Ticket-041: copiar y pegar).</summary>
    public async Task<FolderDto> CopyFolderToAsync(
        FolderDto source, string name, int order, Guid toApp, Guid? toFolder, CancellationToken ct = default)
    {
        var tree = await LoadAsync(ct);
        var copy = await store.CreateFolderAsync(ws, new CreateFolderRequest(toApp, name, toFolder, order), ct);
        await CopyContentsAsync(tree, source.AppId, source.Id, copy.AppId, copy.Id, ct);
        return copy;
    }

    /// <summary>El request, en el mismo lugar, con sus samples.</summary>
    public Task<RequestDto> DuplicateRequestAsync(RequestDto source, string name, int order, CancellationToken ct = default) =>
        CopyRequestToAsync(source, name, order, source.AppId, source.FolderId, ct);

    /// <summary>El request con sus samples, dentro de otra carpeta o App.</summary>
    public async Task<RequestDto> CopyRequestToAsync(
        RequestDto source, string name, int order, Guid toApp, Guid? toFolder, CancellationToken ct = default)
    {
        var tree = await LoadAsync(ct);
        return await CopyRequestAsync(tree, source, name, order, toApp, toFolder, ct);
    }

    public Task<SampleDto> DuplicateSampleAsync(SampleDto source, string name, int order, CancellationToken ct = default) =>
        CopySampleToAsync(source, name, order, source.RequestId, ct);

    /// <summary>El sample, en otro request.</summary>
    public Task<SampleDto> CopySampleToAsync(SampleDto source, string name, int order, Guid toRequest, CancellationToken ct = default) =>
        store.CreateSampleAsync(ws, new CreateSampleRequest(toRequest, name, source.Overrides, order), ct);

    public Task<LoginDto> DuplicateLoginAsync(LoginDto source, string name, CancellationToken ct = default) =>
        store.CreateLoginAsync(ws, new CreateLoginRequest(name, source.Config), ct);

    private sealed record Snapshot(
        IReadOnlyList<FolderDto> Folders, IReadOnlyList<RequestDto> Requests, IReadOnlyList<SampleDto> Samples);

    /// <summary>
    /// Se lee todo antes de crear nada: así la copia no se recorre a sí misma (por ejemplo, al duplicar una carpeta
    /// dentro de la misma carpeta padre).
    /// </summary>
    private async Task<Snapshot> LoadAsync(CancellationToken ct) => new(
        await store.GetFoldersAsync(ws, ct), await store.GetRequestsAsync(ws, ct), await store.GetSamplesAsync(ws, ct));

    private async Task CopyContentsAsync(
        Snapshot tree, Guid fromApp, Guid? fromFolder, Guid toApp, Guid? toFolder, CancellationToken ct)
    {
        foreach (var folder in tree.Folders.Where(f => f.AppId == fromApp && f.ParentFolderId == fromFolder))
        {
            var copy = await store.CreateFolderAsync(ws, new CreateFolderRequest(toApp, folder.Name, toFolder, folder.Order), ct);
            await CopyContentsAsync(tree, fromApp, folder.Id, toApp, copy.Id, ct);
        }

        foreach (var request in tree.Requests.Where(r => r.AppId == fromApp && r.FolderId == fromFolder))
            await CopyRequestAsync(tree, request, request.Name, request.Order, toApp, toFolder, ct);
    }

    private async Task<RequestDto> CopyRequestAsync(
        Snapshot tree, RequestDto source, string name, int order, Guid toApp, Guid? toFolder, CancellationToken ct)
    {
        var copy = await store.CreateRequestAsync(ws, new CreateRequestRequest(toApp, name, source.Spec, toFolder, order), ct);
        foreach (var sample in tree.Samples.Where(s => s.RequestId == source.Id))
            await store.CreateSampleAsync(ws, new CreateSampleRequest(copy.Id, sample.Name, sample.Overrides, sample.Order), ct);
        return copy;
    }
}
