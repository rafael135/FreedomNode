using System.Buffers;
using NSec.Cryptography;

namespace FalconNode.Core.Crypto;

/// <summary>
/// Provides helper methods for cryptographic operations such as key agreement and layer encryption/decryption.
/// </summary>
/// <summary>
/// Provides cryptographic helper methods for encryption, decryption, and hashing operations.
/// </summary>
public static class CryptoHelper
{
    /// <summary>
    /// The key agreement algorithm used for ECDH key exchange.
    /// </summary>
    private static readonly KeyAgreementAlgorithm _ecdh = KeyAgreementAlgorithm.X25519;

    /// <summary>
    /// The key derivation algorithm used for deriving keys from shared secrets.
    /// </summary>
    private static readonly KeyDerivationAlgorithm _kdf = KeyDerivationAlgorithm.HkdfSha256;

    /// <summary>
    /// The AEAD cipher algorithm used for encrypting and decrypting layers.
    /// </summary>
    private static readonly AeadAlgorithm _cipher = AeadAlgorithm.ChaCha20Poly1305;

    /// <summary>
    /// Creates a shared session key using ECDH key agreement.
    /// </summary>
    /// <param name="myPrivateKey">The private key of the local party.</param>
    /// <param name="otherPublicKey">The public key of the remote party.</param>
    /// <returns>A shared secret derived from the key agreement.</returns>
    public static Key CreateSessionKey(Key myPrivateKey, PublicKey otherPublicKey)
    {
        using SharedSecret sharedSecret = _ecdh.Agree(myPrivateKey, otherPublicKey);

        return _kdf.DeriveKey(
            sharedSecret,
            null, // Salt
            null, // Info
            _cipher
        );
    }

    /// <summary>
    /// Encrypts a data layer using the provided session key.
    /// </summary>
    /// <param name="sessionKey">The key used for encryption.</param>
    /// <param name="plainText">The plaintext data to encrypt.</param>
    /// <param name="destination">The buffer to receive the encrypted data.</param>
    public static void EncryptLayer(
        Key sessionKey,
        ReadOnlySpan<byte> plainText,
        Span<byte> destination
    )
    {
        Span<byte> nonce = stackalloc byte[12];
        Random.Shared.NextBytes(nonce);

        nonce.CopyTo(destination.Slice(0, 12));

        _cipher.Encrypt(sessionKey, nonce, default, plainText, destination.Slice(12));
    }

    /// <summary>
    /// Attempts to decrypt an encrypted data layer using the provided session key.
    /// </summary>
    /// <param name="sessionKey">The key used for decryption.</param>
    /// <param name="encryptedPacket">The encrypted data to decrypt.</param>
    /// <param name="decryptedOutput">The buffer to receive the decrypted data.</param>
    /// <returns>True if decryption is successful; otherwise, false.</returns>
    public static bool TryDecryptLayer(
        Key sessionKey,
        ReadOnlySpan<byte> encryptedPacket,
        Span<byte> decryptedOutput
    )
    {
        var nonce = encryptedPacket.Slice(0, 12);
        var cipherText = encryptedPacket.Slice(12);

        return _cipher.Decrypt(sessionKey, nonce, default, cipherText, decryptedOutput);
    }
}
