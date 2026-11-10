using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Amazon.KeyManagementService;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Raf.Server.Confidential;

public sealed class ConfidentialOptions
{
    public const string Section = "Confidential";
    public const string LocalProvider = "local";
    public const string KmsProvider = "kms";

    /// <summary>
    /// Proveedor de la clave maestra: <c>local</c> (la clave viene de la configuración: desarrollo, tests y Railway)
    /// o <c>kms</c> (AWS).
    /// </summary>
    [Required] public string Provider { get; set; } = LocalProvider;

    /// <summary>Clave maestra local: 32 bytes en base64. Nunca se versiona.</summary>
    public string? LocalKey { get; set; }

    /// <summary>
    /// Permite el proveedor <c>local</c> en producción. Se activa solo donde no hay KMS y la clave maestra se guarda
    /// en un lugar protegido, como una variable sellada de Railway. Sin esto, el servidor no arranca en producción
    /// con <c>local</c>.
    /// </summary>
    public bool AllowLocalKeyInProduction { get; set; }

    /// <summary>Id o ARN de la clave KMS. Llega por variable de entorno desde el template.</summary>
    public string? KmsKeyId { get; set; }
}

/// <summary>
/// Envuelve las claves de datos con una clave maestra tomada de la configuración. En producción solo se acepta con
/// <see cref="ConfidentialOptions.AllowLocalKeyInProduction"/> (Railway, con la clave en una variable sellada).
/// </summary>
public sealed class LocalKeyWrapper : IKeyWrapper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _master;

    public LocalKeyWrapper(IOptions<ConfidentialOptions> options)
    {
        var text = options.Value.LocalKey;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Falta Confidential:LocalKey. Correr scripts/dev-setup.ps1.");
        try
        {
            _master = Convert.FromBase64String(text);
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException("Confidential:LocalKey debe ser base64.", e);
        }
        if (_master.Length != 32) throw new InvalidOperationException("Confidential:LocalKey debe tener 32 bytes.");
    }

    public string Provider => ConfidentialOptions.LocalProvider;

    public Task<byte[]> WrapAsync(Guid workspaceId, byte[] dataKey, CancellationToken ct)
    {
        var output = new byte[NonceSize + TagSize + dataKey.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_master, TagSize);
        aes.Encrypt(nonce, dataKey, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), workspaceId.ToByteArray());
        return Task.FromResult(output);
    }

    public Task<byte[]> UnwrapAsync(Guid workspaceId, byte[] wrappedKey, CancellationToken ct)
    {
        var key = new byte[wrappedKey.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_master, TagSize);
        aes.Decrypt(wrappedKey.AsSpan(0, NonceSize), wrappedKey.AsSpan(NonceSize + TagSize),
            wrappedKey.AsSpan(NonceSize, TagSize), key, workspaceId.ToByteArray());
        return Task.FromResult(key);
    }
}

public static class ConfidentialSetup
{
    public static IServiceCollection AddConfidential(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var provider = config[$"{ConfidentialOptions.Section}:Provider"] ?? ConfidentialOptions.LocalProvider;
        services.AddOptions<ConfidentialOptions>().BindConfiguration(ConfidentialOptions.Section).ValidateDataAnnotations();

        if (string.Equals(provider, ConfidentialOptions.KmsProvider, StringComparison.OrdinalIgnoreCase))
        {
            // Credenciales y región salen del entorno: en ECS, del TaskRole y de AWS_REGION.
            services.AddSingleton<IAmazonKeyManagementService>(_ => new AmazonKeyManagementServiceClient());
            services.AddSingleton<IKeyWrapper, KmsKeyWrapper>();
        }
        else if (string.Equals(provider, ConfidentialOptions.LocalProvider, StringComparison.OrdinalIgnoreCase))
        {
            if (env.IsProduction() &&
                !config.GetValue<bool>($"{ConfidentialOptions.Section}:{nameof(ConfidentialOptions.AllowLocalKeyInProduction)}"))
                throw new InvalidOperationException(
                    "El proveedor de claves 'local' no está habilitado en producción: usar 'kms' o, donde no hay KMS y " +
                    "la clave maestra está protegida (por ejemplo una variable sellada de Railway), activar " +
                    "Confidential:AllowLocalKeyInProduction.");
            services.AddSingleton<IKeyWrapper, LocalKeyWrapper>();
        }
        else
        {
            throw new InvalidOperationException($"Proveedor de claves desconocido: '{provider}'.");
        }

        services.AddSingleton<IConfidentialManager, EnvelopeConfidentialManager>();
        return services;
    }

    /// <summary>
    /// Cifra los valores secretos guardados en claro antes de este plan. Es idempotente y no cambia
    /// <c>Version</c> ni <c>ChangeSeq</c>: el contenido visible de la variable sigue siendo el mismo.
    /// </summary>
    public static async Task<int> EncryptLegacySecretsAsync(Data.RafDbContext db, IConfidentialManager confidential, CancellationToken ct = default)
    {
        var legacy = await db.Variables.AsNoTracking()
            .Where(v => v.IsSecret && !v.Value.StartsWith(EnvelopeConfidentialManager.Prefix))
            .Select(v => new { v.Id, v.WorkspaceId, v.Value })
            .ToListAsync(ct);

        var done = 0;
        foreach (var v in legacy)
        {
            var encrypted = await confidential.ProtectAsync(v.WorkspaceId, v.Value, ct);
            // Si alguien la editó mientras tanto, la escritura nueva ya quedó cifrada.
            done += await db.Variables.Where(x => x.Id == v.Id && x.Value == v.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, encrypted), ct);
        }
        return done;
    }
}
