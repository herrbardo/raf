using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Raf.Domain.Requests;
using Raf.Server.Data;
using Testcontainers.PostgreSql;

namespace Raf.Server.Tests;

/// <summary>
/// La migración SharedLogins (Ticket-029) pasa el login propio de cada App a un login compartido. Usa su propio
/// Postgres, porque necesita la base en la migración anterior.
/// </summary>
public sealed class SharedLoginsMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17").Build();

    public ValueTask InitializeAsync() => new(_db.StartAsync());

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private RafDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RafDbContext>().UseNpgsql(_db.GetConnectionString()).Options);

    [Fact]
    public async Task Cada_App_con_login_propio_pasa_a_usar_un_login_compartido_con_su_nombre()
    {
        await using var db = NewContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("AppLogin", TestContext.Current.CancellationToken);

        var ws = Guid.NewGuid();
        var (withLogin, offLogin, withoutLogin, deleted) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var on = AppLogin.Default with { Enabled = true, Request = AppLogin.Default.Request with { Url = "{{base}}/token" } };
        var off = AppLogin.Default with { TokenPath = "$.data.token" };
        string Json(AppLogin l) => JsonSerializer.Serialize(l, RafApp.Json);
        const string none = """{"type":"None"}""";

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "WorkspaceSequences" ("WorkspaceId", "Value") VALUES ({ws}, 10);
            """, TestContext.Current.CancellationToken);
        foreach (var (id, name, login, isDeleted) in new[]
                 {
                     (withLogin, "Pagos", Json(on), false),
                     (offLogin, "Cobros", Json(off), false),
                     (withoutLogin, "Sin login", (string?)null, false),
                     (deleted, "Borrada", Json(on), true),
                 })
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Apps" ("Id", "WorkspaceId", "Name", "Order", "Auth", "Login", "Version", "ChangeSeq", "IsDeleted", "UpdatedAt")
                VALUES ({id}, {ws}, {name}, 0, {none}::jsonb, {login}::jsonb, 3, 5, {isDeleted}, now())
                """, TestContext.Current.CancellationToken);
        }

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        await using var after = NewContext();
        var apps = await after.Apps.ToDictionaryAsync(a => a.Id, TestContext.Current.CancellationToken);
        var logins = await after.Logins.ToDictionaryAsync(l => l.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, logins.Count);

        var pagos = logins[apps[withLogin].LoginId!.Value];
        Assert.Equal(("Pagos", ws, 1L), (pagos.Name, pagos.WorkspaceId, pagos.Version));
        Assert.Equivalent(on, pagos.Config, strict: true);
        var cobros = logins[apps[offLogin].LoginId!.Value];
        Assert.Equivalent(off, cobros.Config, strict: true); // apagado sigue apagado

        Assert.Null(apps[withoutLogin].LoginId);
        Assert.Null(apps[deleted].LoginId);

        // Los cambios se numeran después del último del workspace, para que los clientes los reciban.
        Assert.Equal(4L, apps[withLogin].Version);
        Assert.All(new[] { apps[withLogin].ChangeSeq, apps[offLogin].ChangeSeq, pagos.ChangeSeq, cobros.ChangeSeq }, s => Assert.True(s > 10));
        Assert.Equal(5L, apps[withoutLogin].ChangeSeq);
        Assert.Equal(12L, (await after.WorkspaceSequences.SingleAsync(TestContext.Current.CancellationToken)).Value);
    }
}
