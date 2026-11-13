using Raf.Domain.Requests;

namespace Raf.Domain.Entities;

/// <summary>
/// Base de toda entidad sincronizable. <see cref="Version"/> se usa para concurrencia optimista y
/// <see cref="ChangeSeq"/> es el número de cambio del workspace en que se modificó por última vez.
/// Ambos los asigna la capa de datos al guardar.
/// </summary>
public abstract class SyncEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public long Version { get; set; }
    public long ChangeSeq { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public enum WorkspaceRole { Viewer = 0, Editor = 1, Owner = 2 }

/// <summary>Un workspace es su propio "workspace": <c>WorkspaceId == Id</c>.</summary>
public sealed class Workspace : SyncEntity
{
    public required string Name { get; set; }
}

/// <summary>Membresía de un usuario. El Owner es el único miembro con rol <see cref="WorkspaceRole.Owner"/>.</summary>
public sealed class WorkspaceMember : SyncEntity
{
    public Guid UserId { get; set; }
    public WorkspaceRole Role { get; set; }
}

public sealed class WorkspaceEnvironment : SyncEntity
{
    public required string Name { get; set; }
}

/// <summary>Variable global del workspace (<see cref="EnvironmentId"/> null) o de un environment.</summary>
public sealed class Variable : SyncEntity
{
    public Guid? EnvironmentId { get; set; }
    public required string Key { get; set; }
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
}

public sealed class App : SyncEntity
{
    public required string Name { get; set; }
    public int Order { get; set; }

    /// <summary>La que usan los requests con <see cref="AuthType.Inherit"/>.</summary>
    public AuthSpec Auth { get; set; } = AuthSpec.None;

    /// <summary>Login automático para obtener el token; null si nunca se configuró.</summary>
    public AppLogin? Login { get; set; }
}

public sealed class Folder : SyncEntity
{
    public Guid AppId { get; set; }
    public Guid? ParentFolderId { get; set; }
    public required string Name { get; set; }
    public int Order { get; set; }
}

public sealed class ApiRequest : SyncEntity
{
    public Guid AppId { get; set; }
    public Guid? FolderId { get; set; }
    public required string Name { get; set; }
    public int Order { get; set; }
    public RequestSpec Spec { get; set; } = RequestSpec.Empty;
}

public sealed class Sample : SyncEntity
{
    public Guid RequestId { get; set; }
    public required string Name { get; set; }
    public int Order { get; set; }
    public SampleOverrides Overrides { get; set; } = SampleOverrides.Empty;
}
