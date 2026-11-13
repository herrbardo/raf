using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Raf.Domain.Entities;
using Raf.Domain.Requests;

namespace Raf.Server.Data;

public sealed class RafDbContext(
    DbContextOptions<RafDbContext> options, ICurrentUser? currentUser = null, IChangeNotifier? notifier = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<WorkspaceSequence> WorkspaceSequences => Set<WorkspaceSequence>();
    public DbSet<WorkspaceKey> WorkspaceKeys => Set<WorkspaceKey>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<WorkspaceEnvironment> Environments => Set<WorkspaceEnvironment>();
    public DbSet<Variable> Variables => Set<Variable>();
    public DbSet<App> Apps => Set<App>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<ApiRequest> Requests => Set<ApiRequest>();
    public DbSet<Sample> Samples => Set<Sample>();

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e => e.Property(u => u.DisplayName).HasMaxLength(200));

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WorkspaceSequence>().HasKey(s => s.WorkspaceId);

        b.Entity<WorkspaceKey>(e =>
        {
            e.HasKey(k => k.WorkspaceId);
            e.Property(k => k.Provider).HasMaxLength(50);
        });

        Sync<Workspace>(b).Property(w => w.Name).HasMaxLength(200);

        Sync<WorkspaceMember>(b, e =>
        {
            e.HasIndex(m => new { m.WorkspaceId, m.UserId }).IsUnique();
            e.HasIndex(m => m.UserId);
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        Sync<WorkspaceEnvironment>(b).Property(x => x.Name).HasMaxLength(200);

        Sync<Variable>(b, e =>
        {
            e.Property(v => v.Key).HasMaxLength(200);
            e.HasIndex(v => new { v.WorkspaceId, v.EnvironmentId, v.Key })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("\"IsDeleted\" = false");
        });

        Sync<App>(b, e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            Json(e.Property(x => x.Auth));
            Json(e.Property(x => x.Login));
        });

        Sync<Folder>(b, e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.AppId);
        });

        Sync<ApiRequest>(b, e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.AppId);
            Json(e.Property(x => x.Spec));
        });

        Sync<Sample>(b, e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.RequestId);
            Json(e.Property(x => x.Overrides));
        });
    }

    private static EntityTypeBuilder<T> Sync<T>(ModelBuilder b, Action<EntityTypeBuilder<T>>? configure = null)
        where T : SyncEntity
    {
        var e = b.Entity<T>();
        e.HasKey(x => x.Id);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasIndex(x => new { x.WorkspaceId, x.ChangeSeq });
        configure?.Invoke(e);
        return e;
    }

    private static void Json<T>(PropertyBuilder<T> p) where T : class? =>
        p.HasColumnType("jsonb").HasConversion(
            v => JsonSerializer.Serialize(v, JsonOptions),
            s => JsonSerializer.Deserialize<T>(s, JsonOptions)!,
            new ValueComparer<T>(
                (a, c) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(c, JsonOptions),
                v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!));

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Usar SaveChangesAsync: el sellado de ChangeSeq es asíncrono.");

    /// <summary>
    /// Sella cada entidad sincronizable agregada o modificada: incrementa <c>Version</c> y asigna el
    /// próximo <c>ChangeSeq</c> de su workspace. El contador se incrementa con un upsert dentro de la
    /// misma transacción: el lock de fila serializa las escrituras de un workspace, así ningún cambio con
    /// número menor se confirma después de uno con número mayor.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        var entries = ChangeTracker.Entries<SyncEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .ToList();
        if (entries.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

        await using var ownTx = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(ct) : null;

        var now = DateTimeOffset.UtcNow;
        var stamped = new List<(Guid WorkspaceId, long Seq)>();
        foreach (var group in entries.GroupBy(e => e.Entity.WorkspaceId).OrderBy(g => g.Key))
        {
            var seq = await NextChangeSeqAsync(group.Key, ct);
            stamped.Add((group.Key, seq));
            foreach (var entry in group)
            {
                var entity = entry.Entity;
                if (entry.State == EntityState.Added)
                {
                    entity.Version = 1;
                }
                else
                {
                    var original = entry.Property(x => x.Version).OriginalValue;
                    entity.Version = original + 1;
                }
                entity.ChangeSeq = seq;
                entity.UpdatedAt = now;
                entity.UpdatedBy = currentUser?.UserId;
            }
        }

        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        if (ownTx is not null)
        {
            await ownTx.CommitAsync(ct);
            if (notifier is not null)
                foreach (var (workspaceId, seq) in stamped) await notifier.NotifyAsync(workspaceId, seq);
        }
        return result;
    }

    private async Task<long> NextChangeSeqAsync(Guid workspaceId, CancellationToken ct)
    {
        var values = await Database.SqlQuery<long>($"""
            INSERT INTO "WorkspaceSequences" ("WorkspaceId", "Value") VALUES ({workspaceId}, 1)
            ON CONFLICT ("WorkspaceId") DO UPDATE SET "Value" = "WorkspaceSequences"."Value" + 1
            RETURNING "Value"
            """).ToListAsync(ct);
        return values.Single();
    }
}
