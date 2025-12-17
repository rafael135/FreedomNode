using System.Runtime.InteropServices;

namespace FalconNode.Core.Interop;

/// <summary>
/// Provides safe .NET wrappers for cryptographic operations implemented in the native Rust library <c>freedom_core</c>.
/// </summary>
public static class RustCrypto
{
    private const string DllName = "freedom_core";

    /// <summary>
    /// Derives a shared session key using X25519 key agreement.
    /// </summary>
    /// <param name="myPrivateKey">The private key of the local node (32 bytes).</param>
    /// <param name="otherPublicKey">The public key of the other node (32 bytes).</param>
    /// <param name="output">The output buffer where the derived session key will be stored (32 bytes).</param>
    /// <returns>Returns 1 on success, negative value on failure.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int ffi_create_session_key(
        byte* myPrivateKey,
        byte* otherPublicKey,
        byte* output
    );

    /// <summary>
    /// Verifies the integrity and authenticity of a handshake payload.
    /// </summary>
    /// <param name="data">The handshake payload data.</param>
    /// <param name="len">The length of the handshake payload data.</param>
    /// <returns>Returns 1 if the handshake is valid, otherwise returns a negative value.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int ffi_verify_handshake(byte* data, nuint len);

    /// <summary>
    /// Encrypts a data layer using the provided key.
    /// </summary>
    /// <param name="key">The encryption key.</param>
    /// <param name="plain">The plaintext data to encrypt.</param>
    /// <param name="plainLen">The length of the plaintext data.</param>
    /// <param name="output">The buffer where the encrypted data will be stored.</param>
    /// <param name="outCap">The capacity of the output buffer.</param>
    /// <returns>Returns 1 on success, negative value on failure.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int ffi_encrypt_layer(
        byte* key,
        byte* plain,
        nuint plainLen,
        byte* output,
        nuint outCap
    );

    /// <summary>
    /// Decrypts a data layer using the provided key.
    /// </summary>
    /// <param name="key">The decryption key.</param>
    /// <param name="cipher">The ciphertext data to decrypt.</param>
    /// <param name="cipherLen">The length of the ciphertext data.</param>
    /// <param name="output">The buffer where the decrypted data will be stored.</param>
    /// <param name="outCap">The capacity of the output buffer.</param>
    /// <returns>Returns 1 on success, negative value on failure.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int ffi_decrypt_layer(
        byte* key,
        byte* cipher,
        nuint cipherLen,
        byte* output,
        nuint outCap
    );

    /// <summary>
    /// Calculates the CRC32 checksum of the given data.
    /// </summary>
    /// <param name="data">The data for which to calculate the CRC32 checksum.</param>
    /// <param name="len">The length of the data.</param>
    /// <returns>The CRC32 checksum as an unsigned integer.</returns>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe nuint ffi_calculate_crc32(byte* data, nuint len);

    // --- SAFE WRAPPERS ---

    /// <summary>
    /// Derives a shared session key using X25519 key agreement.
    /// </summary>
    /// <param name="myPrivateKey">Your private key as a 32-byte span.</param>
    /// <param name="otherPublicKey">The other party's public key as a 32-byte span.</param>
    /// <param name="output">The output span where the derived session key will be stored (32 bytes).</param>
    /// <exception cref="ArgumentException">Thrown if the output span is not 32 bytes.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the Rust key derivation fails.</exception>
    public static void CreateSessionKey(
        ReadOnlySpan<byte> myPrivateKey,
        ReadOnlySpan<byte> otherPublicKey,
        Span<byte> output
    )
    {
        if (output.Length != 32)
        {
            throw new ArgumentException("Output span must be 32 bytes for session key.");
        }

        unsafe
        {
            fixed (byte* myPrivPtr = myPrivateKey)
            fixed (byte* otherPubPtr = otherPublicKey)
            fixed (byte* outPtr = output)
            {
                int result = ffi_create_session_key(myPrivPtr, otherPubPtr, outPtr);
                if (result != 1)
                {
                    throw new InvalidOperationException("Rust key derivation failed.");
                }
            }
        }
    }

    /// <summary>
    /// Verifies the integrity and authenticity of a handshake payload.
    /// </summary>
    /// <param name="handshakePayload">The handshake payload to verify.</param>
    /// <returns>True if the handshake is valid; otherwise, false.</returns>
    public static bool VerifyHandshake(ReadOnlySpan<byte> handshakePayload)
    {
        unsafe
        {
            fixed (byte* ptr = handshakePayload)
            {
                int result = ffi_verify_handshake(ptr, (nuint)handshakePayload.Length);
                return result == 1;
            }
        }
    }

    /// <summary>
    /// Encrypts a data layer using the provided key.
    /// </summary>
    /// <remarks>
    /// Output buffer must be at least Plaintext + 28 bytes (12 Nonce + 16 Tag).
    /// </remarks>
    /// <param name="key">The encryption key as a byte span.</param>
    /// <param name="plainText">The plaintext data to encrypt.</param>
    /// <param name="output">The output span where the encrypted data will be stored.</param>
    /// <returns>The number of bytes written to the output span.</returns>
    public static int EncryptLayer(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plainText,
        Span<byte> output
    )
    {
        unsafe
        {
            fixed (byte* keyPtr = key)
            fixed (byte* plainPtr = plainText)
            fixed (byte* outPtr = output)
            {
                return ffi_encrypt_layer(
                    keyPtr,
                    plainPtr,
                    (nuint)plainText.Length,
                    outPtr,
                    (nuint)output.Length
                );
            }
        }
    }

    /// <summary>
    /// Decrypts a data layer using the provided key.
    /// </summary>
    /// <param name="key">The decryption key as a byte span.</param>
    /// <param name="cipherText">The ciphertext data to decrypt.</param>
    /// <param name="output">The output span where the decrypted data will be stored.</param>
    /// <returns>The number of bytes written to the output span.</returns>
    public static int DecryptLayer(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> cipherText,
        Span<byte> output
    )
    {
        unsafe
        {
            fixed (byte* keyPtr = key)
            fixed (byte* cipherPtr = cipherText)
            fixed (byte* outPtr = output)
            {
                return ffi_decrypt_layer(
                    keyPtr,
                    cipherPtr,
                    (nuint)cipherText.Length,
                    outPtr,
                    (nuint)output.Length
                );
            }
        }
    }

    /// <summary>
    /// Calculates the CRC32 checksum of the given data.
    /// </summary>
    /// <param name="data">The data for which to calculate the CRC32 checksum.</param>
    /// <returns>The CRC32 checksum as an unsigned integer.</returns>
    public static uint CalculateCrc32(ReadOnlySpan<byte> data)
    {
        unsafe
        {
            fixed (byte* dataPtr = data)
            {
                return ffi_calculate_crc32(dataPtr, (nuint)data.Length);
            }
        }
    }
}
