using Microsoft.AspNetCore.Identity;

namespace Raf.Server.Data;

/// <summary>Usuario del sistema. El email se usa como nombre de usuario.</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";

    /// <summary>Rol de sistema: gestiona usuarios y puede reasignar owners de workspaces.</summary>
    public bool IsAdmin { get; set; }

    public bool IsDisabled { get; set; }

    /// <summary>true mientras el usuario tenga una contraseña temporal asignada por un admin.</summary>
    public bool MustChangePassword { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Refresh token opaco; solo se guarda su hash SHA-256.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>Contador de cambios por workspace (ver <see cref="RafDbContext"/>).</summary>
public sealed class WorkspaceSequence
{
    public Guid WorkspaceId { get; set; }
    public long Value { get; set; }
}

/// <summary>
/// Clave de datos de un workspace, envuelta (cifrada) por la clave maestra del proveedor. Con ella se cifran los
/// valores de las variables secretas.
/// </summary>
public sealed class WorkspaceKey
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Proveedor que envolvió la clave (por ejemplo "local"); permite detectar una configuración equivocada.</summary>
    public required string Provider { get; set; }

    public required byte[] WrappedKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Usuario autenticado en la request actual; null fuera de una request (migraciones, bootstrap).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
}

/// <summary>Avisa que un workspace cambió. Se invoca después de confirmar la transacción.</summary>
public interface IChangeNotifier
{
    /// <summary>No debe propagar errores de entrega: la escritura ya está confirmada.</summary>
    ValueTask NotifyAsync(Guid workspaceId, long changeSeq);
}

/// <summary>Sin tiempo real: los clientes se enteran por polling hasta el Plan 05.</summary>
public sealed class NoopChangeNotifier : IChangeNotifier
{
    public ValueTask NotifyAsync(Guid workspaceId, long changeSeq) => ValueTask.CompletedTask;
}
