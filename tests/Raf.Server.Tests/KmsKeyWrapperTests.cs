using System.Reflection;
using System.Security.Cryptography;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.Extensions.Options;
using Raf.Server.Confidential;

namespace Raf.Server.Tests;

/// <summary>
/// KMS falso: implementa solo Encrypt y Decrypt, y como KMS exige al desenvolver el mismo contexto de cifrado
/// que se usó al envolver.
/// </summary>
public class FakeKms : DispatchProxy
{
    private readonly byte[] _master = RandomNumberGenerator.GetBytes(32);

    public List<Dictionary<string, string>> Contexts { get; } = [];

    public static (IAmazonKeyManagementService Client, FakeKms Fake) Create()
    {
        var client = DispatchProxy.Create<IAmazonKeyManagementService, FakeKms>();
        return (client, (FakeKms)(object)client);
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "EncryptAsync" => Task.FromResult(Encrypt((EncryptRequest)args![0]!)),
        "DecryptAsync" => Task.FromResult(Decrypt((DecryptRequest)args![0]!)),
        _ => throw new NotSupportedException(method?.Name),
    };

    private byte[] Aad(Dictionary<string, string> context) =>
        System.Text.Encoding.UTF8.GetBytes(string.Join(';', context.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")));

    private EncryptResponse Encrypt(EncryptRequest req)
    {
        Contexts.Add(req.EncryptionContext);
        var plain = req.Plaintext.ToArray();
        var output = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(_master, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), Aad(req.EncryptionContext));
        return new EncryptResponse { CiphertextBlob = new MemoryStream(output), KeyId = req.KeyId };
    }

    private DecryptResponse Decrypt(DecryptRequest req)
    {
        var blob = req.CiphertextBlob.ToArray();
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(_master, 16);
        try
        {
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Aad(req.EncryptionContext));
        }
        catch (CryptographicException)
        {
            throw new InvalidCiphertextException("El contexto de cifrado no coincide.");
        }
        return new DecryptResponse { Plaintext = new MemoryStream(plain), KeyId = req.KeyId };
    }
}

public class KmsKeyWrapperTests
{
    private static KmsKeyWrapper Wrapper(IAmazonKeyManagementService kms) =>
        new(kms, Options.Create(new ConfidentialOptions { Provider = "kms", KmsKeyId = "alias/raf-test" }));

    [Fact]
    public async Task Wraps_with_the_workspace_as_encryption_context()
    {
        var (client, fake) = FakeKms.Create();
        var wrapper = Wrapper(client);
        var ws = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(32);

        var wrapped = await wrapper.WrapAsync(ws, key, default);
        Assert.Equal(key, await wrapper.UnwrapAsync(ws, wrapped, default));
        Assert.Equal(ws.ToString(), Assert.Single(fake.Contexts)[KmsKeyWrapper.ContextKey]);
        Assert.Equal(ConfidentialOptions.KmsProvider, wrapper.Provider);
    }

    [Fact]
    public async Task A_key_wrapped_for_one_workspace_cannot_be_unwrapped_for_another()
    {
        var (client, _) = FakeKms.Create();
        var wrapper = Wrapper(client);
        var wrapped = await wrapper.WrapAsync(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), default);
        await Assert.ThrowsAsync<InvalidCiphertextException>(() => wrapper.UnwrapAsync(Guid.NewGuid(), wrapped, default));
    }

    [Fact]
    public void Requires_a_key_id()
    {
        var (client, _) = FakeKms.Create();
        Assert.Throws<InvalidOperationException>(() =>
            new KmsKeyWrapper(client, Options.Create(new ConfidentialOptions { Provider = "kms" })));
    }
}
