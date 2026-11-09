using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Raf.Server.Data;

namespace Raf.Server.Confidential;

/// <summary>
/// Cifra y descifra valores confidenciales de un workspace (hoy, las variables secretas). No expone detalles del
/// proveedor de claves: el resto del servidor solo ve texto cifrado y texto plano.
/// </summary>
public interface IConfidentialManager
{
    Task<string> ProtectAsync(Guid workspaceId, string plaintext, CancellationToken ct = default);

    /// <summary>Un valor sin el formato cifrado se devuelve tal cual: es un dato anterior a la migración.</summary>
    Task<string> UnprotectAsync(Guid workspaceId, string ciphertext, CancellationToken ct = default);
}

/// <summary>
/// Envuelve y desenvuelve las claves de datos con la clave maestra de un proveedor (configuración local o KMS).
/// El id del workspace viaja como contexto: una clave envuelta para un workspace no sirve para otro.
/// </summary>
public interface IKeyWrapper
{
    /// <summary>Nombre que se guarda junto a cada clave envuelta.</summary>
    string Provider { get; }

    Task<byte[]> WrapAsync(Guid workspaceId, byte[] dataKey, CancellationToken ct);
    Task<byte[]> UnwrapAsync(Guid workspaceId, byte[] wrappedKey, CancellationToken ct);
}

/// <summary>
/// Cifrado por sobre: cada workspace tiene su clave de datos AES-256, guardada envuelta en <c>WorkspaceKeys</c> y
/// mantenida unos minutos en memoria. Los valores se cifran con AES-GCM usando el id del workspace como dato
/// asociado, así un valor copiado a otro workspace no se puede descifrar.
/// </summary>
public sealed class EnvelopeConfidentialManager(IKeyWrapper wrapper, IServiceScopeFactory scopes, TimeProvider clock)
    : IConfidentialManager
{
    public const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, (byte[] Key, DateTimeOffset Expires)> _cache = new();

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public async Task<string> ProtectAsync(Guid workspaceId, string plaintext, CancellationToken ct = default)
    {
        var key = await GetKeyAsync(workspaceId, ct);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Aad(workspaceId));
        return Prefix + Convert.ToBase64String(output);
    }

    public async Task<string> UnprotectAsync(Guid workspaceId, string ciphertext, CancellationToken ct = default)
    {
        if (!IsProtected(ciphertext)) return ciphertext;

        var data = Convert.FromBase64String(ciphertext[Prefix.Length..]);
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("Valor cifrado incompleto.");

        var key = await GetKeyAsync(workspaceId, ct);
        var plain = new byte[data.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain, Aad(workspaceId));
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Aad(Guid workspaceId) => workspaceId.ToByteArray();

    private async Task<byte[]> GetKeyAsync(Guid workspaceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(workspaceId, out var cached) && cached.Expires > now) return cached.Key;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RafDbContext>();

        var stored = await db.WorkspaceKeys.AsNoTracking().FirstOrDefaultAsync(k => k.WorkspaceId == workspaceId, ct);
        if (stored is null)
        {
            // Dos requests pueden crear la clave a la vez: gana la primera que se inserta y la otra la relee.
            var fresh = RandomNumberGenerator.GetBytes(32);
            var wrapped = await wrapper.WrapAsync(workspaceId, fresh, ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "WorkspaceKeys" ("WorkspaceId", "Provider", "WrappedKey", "CreatedAt")
                VALUES ({workspaceId}, {wrapper.Provider}, {wrapped}, {now})
                ON CONFLICT ("WorkspaceId") DO NOTHING
                """, ct);
            stored = await db.WorkspaceKeys.AsNoTracking().FirstAsync(k => k.WorkspaceId == workspaceId, ct);
        }

        if (stored.Provider != wrapper.Provider)
            throw new InvalidOperationException(
                $"La clave del workspace fue creada con el proveedor '{stored.Provider}' y el servidor usa '{wrapper.Provider}'.");

        var key = await wrapper.UnwrapAsync(workspaceId, stored.WrappedKey, ct);
        _cache[workspaceId] = (key, now + CacheFor);
        return key;
    }
}
