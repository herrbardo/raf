using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.Extensions.Options;

namespace Raf.Server.Confidential;

/// <summary>
/// Envuelve las claves de datos con una clave de AWS KMS. El id del workspace va como contexto de cifrado: KMS
/// exige el mismo contexto para desenvolver, y CloudTrail registra cada uso con ese workspace.
/// </summary>
public sealed class KmsKeyWrapper(IAmazonKeyManagementService kms, IOptions<ConfidentialOptions> options) : IKeyWrapper
{
    public const string ContextKey = "workspaceId";

    private readonly string _keyId = string.IsNullOrWhiteSpace(options.Value.KmsKeyId)
        ? throw new InvalidOperationException("Falta Confidential:KmsKeyId.")
        : options.Value.KmsKeyId;

    public string Provider => ConfidentialOptions.KmsProvider;

    private static Dictionary<string, string> Context(Guid workspaceId) => new() { [ContextKey] = workspaceId.ToString() };

    public async Task<byte[]> WrapAsync(Guid workspaceId, byte[] dataKey, CancellationToken ct)
    {
        var response = await kms.EncryptAsync(new EncryptRequest
        {
            KeyId = _keyId,
            Plaintext = new MemoryStream(dataKey),
            EncryptionContext = Context(workspaceId),
        }, ct);
        return response.CiphertextBlob.ToArray();
    }

    public async Task<byte[]> UnwrapAsync(Guid workspaceId, byte[] wrappedKey, CancellationToken ct)
    {
        var response = await kms.DecryptAsync(new DecryptRequest
        {
            KeyId = _keyId,
            CiphertextBlob = new MemoryStream(wrappedKey),
            EncryptionContext = Context(workspaceId),
        }, ct);
        return response.Plaintext.ToArray();
    }
}
