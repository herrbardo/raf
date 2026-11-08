using Npgsql;

namespace Raf.Server.Infrastructure;

public static class DatabaseConfig
{
    /// <summary>
    /// Usa <c>ConnectionStrings:Raf</c> si está definida (desarrollo, tests). Si no, arma la conexión con
    /// <c>Db:Host</c>, <c>Db:Port</c>, <c>Db:Name</c>, <c>Db:User</c>, <c>Db:Password</c> y <c>Db:SslMode</c>:
    /// en ECS usuario y contraseña llegan por separado desde Secrets Manager.
    /// </summary>
    public static string GetConnectionString(IConfiguration config)
    {
        var cs = config.GetConnectionString("Raf");
        if (!string.IsNullOrWhiteSpace(cs)) return cs;

        var db = config.GetSection("Db");
        if (string.IsNullOrWhiteSpace(db["Host"]))
            throw new InvalidOperationException("Falta ConnectionStrings:Raf o Db:Host.");

        return new NpgsqlConnectionStringBuilder
        {
            Host = db["Host"],
            Port = db.GetValue("Port", 5432),
            Database = db["Name"] ?? "raf",
            Username = db["User"],
            Password = db["Password"],
            SslMode = Enum.Parse<SslMode>(db["SslMode"] ?? nameof(SslMode.Require), ignoreCase: true),
        }.ConnectionString;
    }
}
