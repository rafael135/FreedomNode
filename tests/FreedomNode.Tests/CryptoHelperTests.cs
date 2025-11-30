using FalconNode.Core.Crypto;
using NSec.Cryptography;

namespace FreedomNode.Tests;

public class CryptoHelperTests
{
    [Fact]
    public void EncryptAndDecryptLayer_RoundTrip()
    {
        var aKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var bKey = Key.Create(KeyAgreementAlgorithm.X25519);

        // Derive symmetric session keys for both sides
        using var session1 = CryptoHelper.CreateSessionKey(aKey, bKey.PublicKey);
        using var session2 = CryptoHelper.CreateSessionKey(bKey, aKey.PublicKey);

        byte[] plain = System.Text.Encoding.UTF8.GetBytes("secret layer payload");
        byte[] encrypted = new byte[plain.Length + 28]; // 12 nonce + 16 tag

        CryptoHelper.EncryptLayer(session1, plain, encrypted);

        byte[] decrypted = new byte[plain.Length];
        bool ok = CryptoHelper.TryDecryptLayer(session2, encrypted, decrypted);

        Assert.True(ok);
        Assert.Equal(plain, decrypted);
    }
}
