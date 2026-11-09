using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Raf.Server.Confidential;
using Raf.Server.Data;

namespace Raf.Server.Admin;

public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";

    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
    public string AdminDisplayName { get; set; } = "Administrador";

    /// <summary>Si es true, el admin inicial debe cambiar la contraseña en su primer login.</summary>
    public bool AdminMustChangePassword { get; set; } = true;
}

public static class Bootstrap
{
    /// <summary>
    /// Aplica migraciones y cifra los secretos heredados (si <c>Database:MigrateOnStartup</c>), y crea el primer admin cuando no existe ninguno.
    /// </summary>
    public static async Task InitializeAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Raf.Bootstrap");
        var db = sp.GetRequiredService<RafDbContext>();

        if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
        {
            log.LogInformation("Aplicando migraciones…");
            await db.Database.MigrateAsync();

            // Parte de la migración de datos: los secretos guardados antes del cifrado pasan a estar cifrados.
            var encrypted = await ConfidentialSetup.EncryptLegacySecretsAsync(db, sp.GetRequiredService<IConfidentialManager>());
            if (encrypted > 0) log.LogInformation("Variables secretas cifradas: {Count}", encrypted);
        }

        if (await db.Users.AnyAsync(u => u.IsAdmin)) return;

        var opt = app.Configuration.GetSection(BootstrapOptions.Section).Get<BootstrapOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(opt.AdminEmail) || string.IsNullOrWhiteSpace(opt.AdminPassword))
        {
            log.LogWarning("No hay administradores y faltan Bootstrap:AdminEmail / Bootstrap:AdminPassword: nadie podrá crear usuarios.");
            return;
        }

        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var admin = new AppUser
        {
            Id = Guid.CreateVersion7(),
            Email = opt.AdminEmail.Trim(),
            UserName = opt.AdminEmail.Trim(),
            DisplayName = opt.AdminDisplayName,
            IsAdmin = true,
            MustChangePassword = opt.AdminMustChangePassword,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var result = await users.CreateAsync(admin, opt.AdminPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "No se pudo crear el admin inicial: " + string.Join("; ", result.Errors.Select(e => e.Description)));

        log.LogInformation("Admin inicial creado: {Email}", admin.Email);
    }
}
