using System.Text.Json;
using System.Text.Json.Nodes;
using Raf.Client.Core.Api;
using Raf.Contracts;

namespace Raf.Client.Core.Sync;

public enum SyncState { Synced, Pending, Offline, Conflict }

public sealed record SyncStatus(SyncState State, int Pending, int Conflicts);

public enum ConflictReason
{
    /// <summary>Otro usuario modificó el elemento.</summary>
    Version,

    /// <summary>La clave ya existe en ese ámbito.</summary>
    Duplicate,

    /// <summary>El elemento o uno de sus padres fue eliminado en el servidor.</summary>
    Missing,

    /// <summary>El servidor rechazó los datos.</summary>
    Rejected,

    /// <summary>El usuario ya no tiene permiso para editar.</summary>
    Forbidden,
}

/// <summary>Un cambio que necesita una decisión del usuario. Los valores secretos llegan enmascarados.</summary>
public sealed record SyncConflict(
    long Id,
    Guid WorkspaceId,
    string WorkspaceName,
    ChangeKind Kind,
    Guid EntityId,
    string Name,
    ConflictReason Reason,
    string Message,
    string LocalJson,
    string? RemoteJson)
{
    public bool CanUseLocal => Reason == ConflictReason.Version;
    public bool CanEdit => Reason is ConflictReason.Version or ConflictReason.Duplicate or ConflictReason.Rejected;
    public bool CanRecreate => Reason == ConflictReason.Missing;
}

/// <summary>El usuario perdió el acceso a un workspace.</summary>
public sealed record WorkspaceLost(Guid Id, string Name, int PendingChanges);

/// <summary>Avisa que puede haber cambios en el servidor. El tiempo real llega en el Plan 05.</summary>
public interface IChangeSubscriber : IDisposable
{
    event Action? ChangeAvailable;
    void Start();
}

/// <summary>Sin tiempo real: se consulta cada cierto intervalo.</summary>
public sealed class PollingChangeSubscriber(TimeSpan interval, TimeProvider clock) : IChangeSubscriber
{
    private ITimer? _timer;

    public event Action? ChangeAvailable;

    public void Start() => _timer ??= clock.CreateTimer(_ => ChangeAvailable?.Invoke(), null, interval, interval);

    public void Dispose() => _timer?.Dispose();
}

/// <summary>Lo que depende del tipo concreto de cada DTO sincronizable.</summary>
internal static class SyncItems
{
    private static readonly JsonSerializerOptions Indented = new(ClientJson.Options) { WriteIndented = true };

    public const string SecretMask = "••••••••";

    public static string Serialize(object dto) => JsonSerializer.Serialize(dto, dto.GetType(), ClientJson.Options);

    public static object Deserialize(ChangeKind kind, string json) => kind switch
    {
        ChangeKind.Member => JsonSerializer.Deserialize<MemberDto>(json, ClientJson.Options)!,
        ChangeKind.Environment => JsonSerializer.Deserialize<EnvironmentDto>(json, ClientJson.Options)!,
        ChangeKind.Variable => JsonSerializer.Deserialize<VariableDto>(json, ClientJson.Options)!,
        ChangeKind.App => JsonSerializer.Deserialize<AppDto>(json, ClientJson.Options)!,
        ChangeKind.Folder => JsonSerializer.Deserialize<FolderDto>(json, ClientJson.Options)!,
        ChangeKind.Request => JsonSerializer.Deserialize<RequestDto>(json, ClientJson.Options)!,
        ChangeKind.Sample => JsonSerializer.Deserialize<SampleDto>(json, ClientJson.Options)!,
        ChangeKind.Workspace => JsonSerializer.Deserialize<WorkspaceDto>(json, ClientJson.Options)!,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static Guid IdOf(object dto) => dto switch
    {
        MemberDto x => x.UserId,
        EnvironmentDto x => x.Id,
        VariableDto x => x.Id,
        AppDto x => x.Id,
        FolderDto x => x.Id,
        RequestDto x => x.Id,
        SampleDto x => x.Id,
        WorkspaceDto x => x.Id,
        _ => throw new ArgumentException(dto.GetType().Name),
    };

    public static long VersionOf(object dto) => dto switch
    {
        MemberDto x => x.Version,
        EnvironmentDto x => x.Version,
        VariableDto x => x.Version,
        AppDto x => x.Version,
        FolderDto x => x.Version,
        RequestDto x => x.Version,
        SampleDto x => x.Version,
        WorkspaceDto x => x.Version,
        _ => throw new ArgumentException(dto.GetType().Name),
    };

    public static string NameOf(object dto) => dto switch
    {
        MemberDto x => x.DisplayName,
        EnvironmentDto x => x.Name,
        VariableDto x => x.Key,
        AppDto x => x.Name,
        FolderDto x => x.Name,
        RequestDto x => x.Name,
        SampleDto x => x.Name,
        WorkspaceDto x => x.Name,
        _ => "",
    };

    /// <summary>Elementos que tienen que existir para que este exista.</summary>
    public static IReadOnlyList<Guid> ParentsOf(object dto) => dto switch
    {
        VariableDto { EnvironmentId: { } env } => [env],
        FolderDto { ParentFolderId: { } parent } x => [x.AppId, parent],
        FolderDto x => [x.AppId],
        RequestDto { FolderId: { } folder } x => [x.AppId, folder],
        RequestDto x => [x.AppId],
        SampleDto x => [x.RequestId],
        _ => [],
    };

    public static object WithMeta(object dto, long version, long changeSeq) => dto switch
    {
        EnvironmentDto x => x with { Version = version, ChangeSeq = changeSeq },
        VariableDto x => x with { Version = version, ChangeSeq = changeSeq },
        AppDto x => x with { Version = version, ChangeSeq = changeSeq },
        FolderDto x => x with { Version = version, ChangeSeq = changeSeq },
        RequestDto x => x with { Version = version, ChangeSeq = changeSeq },
        SampleDto x => x with { Version = version, ChangeSeq = changeSeq },
        _ => dto,
    };

    /// <summary>Copia con otro id y con los padres que cambiaron de id.</summary>
    public static object Remap(object dto, Guid newId, IReadOnlyDictionary<Guid, Guid> map)
    {
        Guid To(Guid id) => map.GetValueOrDefault(id, id);
        Guid? ToOpt(Guid? id) => id is { } v ? To(v) : null;
        return dto switch
        {
            EnvironmentDto x => x with { Id = newId },
            VariableDto x => x with { Id = newId, EnvironmentId = ToOpt(x.EnvironmentId) },
            AppDto x => x with { Id = newId },
            FolderDto x => x with { Id = newId, AppId = To(x.AppId), ParentFolderId = ToOpt(x.ParentFolderId) },
            RequestDto x => x with { Id = newId, AppId = To(x.AppId), FolderId = ToOpt(x.FolderId) },
            SampleDto x => x with { Id = newId, RequestId = To(x.RequestId) },
            _ => throw new ArgumentException(dto.GetType().Name),
        };
    }

    /// <summary>Toma lo editable de <paramref name="edited"/> y conserva la identidad y la ubicación de <paramref name="current"/>.</summary>
    public static object KeepIdentity(object edited, object current) => (edited, current) switch
    {
        (EnvironmentDto e, EnvironmentDto c) => e with { Id = c.Id, Version = c.Version, ChangeSeq = c.ChangeSeq },
        (VariableDto e, VariableDto c) => e with
        {
            Id = c.Id, EnvironmentId = c.EnvironmentId, Version = c.Version, ChangeSeq = c.ChangeSeq,
            Value = e.Value == SecretMask ? c.Value : e.Value,
        },
        (AppDto e, AppDto c) => e with { Id = c.Id, Version = c.Version, ChangeSeq = c.ChangeSeq },
        (FolderDto e, FolderDto c) => e with
        {
            Id = c.Id, AppId = c.AppId, ParentFolderId = c.ParentFolderId, Version = c.Version, ChangeSeq = c.ChangeSeq,
        },
        (RequestDto e, RequestDto c) => e with
        {
            Id = c.Id, AppId = c.AppId, FolderId = c.FolderId, Version = c.Version, ChangeSeq = c.ChangeSeq,
        },
        (SampleDto e, SampleDto c) => e with { Id = c.Id, RequestId = c.RequestId, Version = c.Version, ChangeSeq = c.ChangeSeq },
        _ => throw new ApiException(System.Net.HttpStatusCode.BadRequest, "El contenido editado no corresponde a este elemento."),
    };

    /// <summary>Compara el contenido, sin tener en cuenta la versión ni el número de cambio.</summary>
    public static bool SameContent(object a, object b) =>
        Serialize(WithMeta(a, 0, 0)) == Serialize(WithMeta(b, 0, 0));

    /// <summary>JSON para mostrar: legible y sin revelar secretos ni datos internos de versión.</summary>
    public static string ForDisplay(object dto)
    {
        if (dto is VariableDto { IsSecret: true, Value: not null } secret) dto = secret with { Value = SecretMask };
        var node = JsonNode.Parse(Serialize(dto))!.AsObject();
        node.Remove("changeSeq");
        return node.ToJsonString(Indented);
    }
}

/// <summary>Comparación de contenido para quien solo conoce los DTOs, como las pestañas abiertas.</summary>
public static class SyncContent
{
    public static bool Same(object a, object b) => SyncItems.SameContent(a, b);
}
