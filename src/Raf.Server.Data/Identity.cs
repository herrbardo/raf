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

/// <summary>Usuario autenticado en la request actual; null fuera de una request (migraciones, bootstrap).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
}
