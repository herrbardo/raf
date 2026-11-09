using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Raf.Contracts;
using Raf.Domain.Entities;

namespace Raf.Client.Core.Sync;

internal sealed class CachedWorkspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public WorkspaceRole Role { get; set; }
    public long Version { get; set; }
    public long ChangeSeq { get; set; }

    /// <summary>Último número de cambio ya aplicado desde el servidor.</summary>
    public long LastSeq { get; set; }

    /// <summary>El usuario perdió el acceso y hay cambios pendientes: espera a que lo confirme para borrarse.</summary>
    public bool AccessLost { get; set; }
}

internal sealed class CachedItem
{
    public Guid WorkspaceId { get; set; }
    public ChangeKind Kind { get; set; }
    public Guid Id { get; set; }

    /// <summary>El DTO en JSON, con el valor de las variables secretas cifrado.</summary>
    public string Json { get; set; } = "";

    public long Version { get; set; }

    /// <summary>
    /// Versión desde la que todos los cambios fueron propios. Una pestaña abierta en una versión anterior a la actual
    /// pero no anterior a esta no perdió nada: la diferencia la generó este mismo cliente.
    /// </summary>
    public long OwnSince { get; set; }

    /// <summary>Ids de los elementos de los que depende, separados por comas.</summary>
    public string Parents { get; set; } = "";

    /// <summary>
    /// Borrado, pero conservado mientras haya cambios pendientes en el workspace: hace falta para poder recrearlo si un
    /// hijo creado sin conexión se queda sin padre.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>El elemento que lo reemplazó al recrearlo.</summary>
    public Guid? ReplacedBy { get; set; }
}

internal enum OutboxOp { Create, Update, Delete }

internal enum OutboxState { Pending, Conflict }

/// <summary>Un cambio por enviar. Los datos a enviar son siempre el estado actual del elemento en la caché.</summary>
internal sealed class OutboxEntry
{
    public long Seq { get; set; }
    public Guid WorkspaceId { get; set; }
    public ChangeKind Kind { get; set; }
    public Guid EntityId { get; set; }
    public OutboxOp Op { get; set; }

    /// <summary>Versión del servidor sobre la que se hizo el cambio.</summary>
    public long BaseVersion { get; set; }

    /// <summary>Aumenta con cada edición, para saber si el elemento cambió mientras se enviaba.</summary>
    public int Revision { get; set; }

    public OutboxState State { get; set; }
    public ConflictReason? Reason { get; set; }
    public string? Message { get; set; }

    /// <summary>La versión del servidor en conflicto, en el mismo formato que <see cref="CachedItem.Json"/>.</summary>
    public string? RemoteJson { get; set; }
}

internal sealed class CacheDb(DbContextOptions<CacheDb> options) : DbContext(options)
{
    public DbSet<CachedWorkspace> Workspaces => Set<CachedWorkspace>();
    public DbSet<CachedItem> Items => Set<CachedItem>();
    public DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CachedItem>(e =>
        {
            e.HasKey(x => new { x.WorkspaceId, x.Kind, x.Id });
            e.HasIndex(x => new { x.WorkspaceId, x.Id });
        });
        b.Entity<OutboxEntry>(e =>
        {
            e.HasKey(x => x.Seq);
            e.HasIndex(x => new { x.WorkspaceId, x.EntityId });
        });
    }
}

/// <summary>Cifra los valores secretos en disco con una clave guardada en el almacén seguro del sistema (D2).</summary>
internal sealed class SecretCipher(byte[] key)
{
    public const string Prefix = "enc1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string Protect(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(output);
    }

    /// <summary>Devuelve null si el valor no se puede descifrar, por ejemplo porque se perdió la clave.</summary>
    public string? Unprotect(string stored)
    {
        try
        {
            var data = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = new byte[data.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
