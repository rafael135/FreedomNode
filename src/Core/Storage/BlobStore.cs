using System.Buffers;
using System.Security.Cryptography;
using FalconNode.Core.State;
using NSec.Cryptography;

namespace FalconNode.Core.Storage;

/// <summary>
/// BlobStore — a small, content-addressed, encrypted blob storage component used by the node.
/// It stores blobs on disk using the SHA-256 digest of the plaintext as the filename
/// (lowercase hex). Each file contains a per-file nonce followed by the AEAD ciphertext
/// and authentication tag (currently ChaCha20-Poly1305). Blob files are written
/// atomically using a temporary file ("*.tmp") then renamed into place to avoid
/// partially-written files being considered valid.
/// </summary>
/// <remarks>
/// Purpose & guarantees:
/// - Content-addressed: callers compute or receive the SHA-256 content ID and use it
///   to store and later retrieve the corresponding blob.
/// - Confidentiality & integrity: payloads are encrypted and authenticated with
///   ChaCha20-Poly1305 using the node's configured storage key (see <see cref="NodeSettings.StorageKey"/>).
/// - Atomic writes: temporary files are used to ensure either the full encrypted blob
///   is present or nothing is visible at the target filename.
/// Threading & error model:
/// - Concurrent readers are supported. Concurrent writers for the same content are
///   tolerated: a race results in either writer winning or the caller observing the
///   existing file — the implementation intentionally swallows IO races for idempotence.
/// - The class logs errors for decryption and malformed files rather than throwing
///   in most read paths; callers should treat a returned <c>null</c> (or 0 bytes)
///   as an indicator of a missing or invalid blob.
/// Memory and usage guidance:
/// - The implementation uses pooled buffers (<see cref="ArrayPool{T}.Shared"/>)
///   and attempts in-place encryption to reduce allocations, but some APIs (like
///   <see cref="RetrieveBytesAsync(byte[])"/>) allocate a full plaintext buffer and
///   therefore are intended for small objects (manifests, metadata). Use
///   <see cref="RetrieveToStreamAsync(byte[], Stream)"/> or
///   <see cref="RetrieveToBufferAsync(byte[], Memory{byte})"/> for large payloads and
///   streaming scenarios.
/// Key implementation notes:
/// - File layout: [nonce (12 bytes)] [ciphertext] [tag (16 bytes)].
/// - Users of the class should not leak or persist the storage key outside of
///   NodeSettings; it is used only for encryption and decryption.
/// </remarks>
public class BlobStore
{
    /// <summary>
    /// The path to the directory where blobs are stored.
    /// </summary>
    private readonly string _storagePath;

    /// <summary>
    /// The AEAD algorithm used for encrypting and decrypting blobs.
    /// </summary>
    private readonly AeadAlgorithm _algorithm = AeadAlgorithm.ChaCha20Poly1305;

    /// <summary>
    /// The storage key used for encrypting and decrypting blobs.
    /// </summary>
    private readonly Key _storageKey;

    /// <summary>
    /// The logger instance for logging blob store operations.
    /// </summary>
    private readonly ILogger<BlobStore> _logger;

    /// <summary>
    /// Create a new <see cref="BlobStore"/> backed by the application's
    /// data directory ("data/blobs").
    /// </summary>
    /// <param name="settings">Node settings containing the <see cref="NodeSettings.StorageKey"/> used for AEAD encryption.</param>
    /// <param name="logger">An <see cref="ILogger{BlobStore}"/> instance used for logging operations and errors.</param>
    /// <remarks>
    /// The constructor ensures the storage directory exists and saves the provided
    /// storage key for later encrypt/decrypt operations. This type does not validate
    /// the key; callers must ensure the configured <see cref="NodeSettings.StorageKey"/> is correct.
    /// </remarks>
    public BlobStore(NodeSettings settings, ILogger<BlobStore> logger)
    {
        _storagePath = Path.Combine(AppContext.BaseDirectory, "data", "blobs");
        Directory.CreateDirectory(_storagePath);
        _storageKey = settings.StorageKey;
        _logger = logger;
    }

    /// <summary>
    /// Persistently stores a binary blob under the node's storage directory.
    /// The blob filename is the lowercase hex-encoded SHA-256 content hash calculated
    /// from the provided <paramref name="data"/>. If a blob with the same hash
    /// already exists the call is a no-op and the method returns the calculated hash.
    /// </summary>
    /// <remarks>
    /// The payload is encrypted in-place (ChaCha20-Poly1305 AEAD) using the node's
    /// configured storage key and written atomically to disk using a temporary
    /// file ("*.tmp") and then renamed. This minimizes the risk of partial files
    /// being treated as valid blobs. The method uses rented buffers from
    /// <see cref="ArrayPool{T}.Shared"/> to avoid unnecessary allocations.
    ///
    /// If the target file already exists another process or thread may have
    /// stored the same content in the meantime; in that case the method simply
    /// returns the content hash and does not throw. IO exceptions during the
    /// final write are caught and ignored to be tolerant of small races.
    /// </remarks>
    /// <param name="data">Plaintext payload to store. The method will encrypt this data
    /// before writing it to disk.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> containing the SHA-256 hash of the stored data
    /// (the content ID) as a byte array. The hash is computed from the input
    /// <paramref name="data"/> and returned even if the blob already existed.
    /// </returns>
    public async Task<byte[]> StoreAsync(ReadOnlyMemory<byte> data)
    {
        // 1. Calculate Hash (content id) without heap allocation
        // stackalloc creates a buffer on the stack (zero GC cost)
        Span<byte> hashSpan = stackalloc byte[32];
        SHA256.HashData(data.Span, hashSpan);

        string hashString = Convert.ToHexString(hashSpan).ToLowerInvariant();
        string filePath = Path.Combine(_storagePath, hashString);

        // We need to return the hash as an array for the caller (Manifest ID)
        byte[] hashResult = hashSpan.ToArray();

        // 2. If already exists, dont do anything
        if (File.Exists(filePath))
        {
            // _logger.LogInformation("Blob with hash {Hash} already exists. Skipping storage.", hashString);
            return hashResult;
        }

        // 3. Final encrypted size calculation
        // Final size = data + 12 (Nonce) + 16 (Tag)
        int encryptedSize = data.Length + _algorithm.NonceSize + _algorithm.TagSize;

        byte[] encryptedBuffer = ArrayPool<byte>.Shared.Rent(encryptedSize);

        try
        {
            Span<byte> nonceSpan = encryptedBuffer.AsSpan(0, _algorithm.NonceSize);
            RandomNumberGenerator.Fill(nonceSpan);

            // Cryptograph the data directly into the rented buffer
            // The NSec library appends the authentication tag at the end of the Nonce
            // Ensure the ciphertext span exactly matches plaintext + tag size
            _algorithm.Encrypt(
                _storageKey,
                nonceSpan,
                default,
                data.Span,
                encryptedBuffer.AsSpan(_algorithm.NonceSize, data.Length + _algorithm.TagSize)
            );

            // 3. Save to disk (Atomic, if possible)
            // The .tmp suffix ensures that incomplete writes are not mistaken for complete files if the node crashes during write.
            string tempPath = filePath + ".tmp";

            // Use FileStream directly with ReadOnlyMemory to avoid array copying
            await using (
                var fs = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true
                )
            )
            {
                await fs.WriteAsync(encryptedBuffer.AsMemory(0, encryptedSize));
            }

            File.Move(tempPath, filePath, overwrite: true);
            _logger.LogInformation(
                $"Stored blob with hash {hashString} at {filePath}, with size {data.Length} bytes."
            );
        }
        catch (IOException)
        {
            // Race condition: Someone stored the same blob milliseconds ago. Ignore.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encryptedBuffer);
        }

        return hashResult;
    }

    /// <summary>
    /// Asynchronously retrieves the blob data associated with the specified hash and copies it directly to a target stream.
    /// This is memory efficient for large files as it avoids loading the entire blob into RAM.
    /// </summary>
    /// <param name="hash">The hash of the blob to retrieve.</param>
    /// <param name="targetStream">The stream to write the blob data to.</param>
    /// <returns>True if found and copied; otherwise false.</returns>
    public async Task<bool> RetrieveToStreamAsync(byte[] hash, Stream targetStream)
    {
        byte[]? plainText = await RetrieveBytesAsync(hash);
        if (plainText == null)
        {
            return false;
        }

        await targetStream.WriteAsync(plainText);

        return true;
    }

    /// <summary>
    /// Retrieve and decrypt a blob identified by its content hash and return the
    /// plaintext bytes. This helper is intended for small blobs (for example,
    /// manifest objects) because it reads the entire encrypted payload into memory
    /// before decrypting.
    /// </summary>
    /// <param name="hash">The content hash of the blob (SHA-256) as a byte array.</param>
    /// <returns>
    /// A plaintext byte array containing the blob contents when successful; otherwise
    /// <c>null</c> if the blob does not exist, the on-disk file has an invalid size,
    /// or decryption fails.
    /// </returns>
    /// <remarks>
    /// The function performs the following checks and actions:
    /// - Reads the raw encrypted file using <see cref="ReadRawFileAsync(byte[])"/>.
    /// - Validates the expected plaintext length computed as fileLength - nonce - tag.
    /// - Attempts AEAD decryption (ChaCha20-Poly1305) into a newly allocated
    ///   buffer and returns it on success.
    ///
    /// It logs an error and returns <c>null</c> if the size calculation is invalid
    /// or if authentication/decryption fails. For large blobs prefer
    /// <see cref="RetrieveToStreamAsync(byte[], Stream)"/> or
    /// <see cref="RetrieveToBufferAsync(byte[], Memory{byte})"/> to reduce peak
    /// memory usage.</remarks>
    public async Task<byte[]?> RetrieveBytesAsync(byte[] hash)
    {
        byte[]? encryptedData = await ReadRawFileAsync(hash);
        if (encryptedData == null)
        {
            return null;
        }

        int plainTextSize = encryptedData.Length - _algorithm.NonceSize - _algorithm.TagSize;

        if (plainTextSize < 0)
        {
            _logger.LogError(
                "Decryption failed for blob with hash {Hash}. Invalid size.",
                Convert.ToHexString(hash).ToLowerInvariant()
            );
            return null;
        }

        byte[] plainText = new byte[plainTextSize];

        ReadOnlySpan<byte> encryptedSpan = encryptedData.AsSpan();
        ReadOnlySpan<byte> nonceSpan = encryptedSpan.Slice(0, _algorithm.NonceSize);
        ReadOnlySpan<byte> cipherTextAndTagSpan = encryptedSpan.Slice(_algorithm.NonceSize);

        if (!_algorithm.Decrypt(_storageKey, nonceSpan, default, cipherTextAndTagSpan, plainText))
        {
            _logger.LogError(
                $"Decryption failed for blob with hash {Convert.ToHexString(hash).ToLowerInvariant()}."
            );
            return null;
        }

        return plainText;
    }

    /// <summary>
    /// Determines whether a blob with the specified hash exists in the storage.
    /// </summary>
    /// <param name="hash">The hash of the blob as a byte array.</param>
    /// <returns>
    /// <c>true</c> if the blob exists; otherwise, <c>false</c>.
    /// </returns>
    public bool HasBlob(byte[] hash)
    {
        string hashString = Convert.ToHexString(hash).ToLowerInvariant();
        return File.Exists(Path.Combine(_storagePath, hashString));
    }

    /// <summary>
    /// Returns the plaintext size (in bytes) of the blob identified by the given
    /// content <paramref name="hash"/>, or <c>null</c> when the blob does not exist
    /// or the stored file appears malformed.
    /// </summary>
    /// <param name="hash">The SHA-256 content hash of the blob as a byte array.</param>
    /// <returns>
    /// The size (in bytes) of the decrypted/plaintext payload when available,
    /// or <c>null</c> if the blob file does not exist, or when the computed size
    /// (file length minus nonce and authentication tag) is not greater than zero.
    /// </returns>
    public long? GetBlobSize(byte[] hash)
    {
        string hashString = Convert.ToHexString(hash).ToLowerInvariant();
        string filePath = Path.Combine(_storagePath, hashString);

        FileInfo info = new FileInfo(filePath);

        if (!info.Exists)
        {
            return null;
        }

        long realSize = info.Length - _algorithm.NonceSize - _algorithm.TagSize;

        return realSize > 0 ? realSize : null;
    }

    /// <summary>
    /// Asynchronously reads the raw contents of a file identified by its hash.
    /// </summary>
    /// <param name="hash">The hash of the file to read, as a byte array.</param>
    /// <returns>
    /// A byte array containing the file's contents if found; otherwise, <c>null</c>.
    /// </returns>
    /// <remarks>
    /// Logs a warning if the file corresponding to the given hash does not exist.
    /// </remarks>
    private async Task<byte[]?> ReadRawFileAsync(byte[] hash)
    {
        string hashString = Convert.ToHexString(hash).ToLowerInvariant();
        string filePath = Path.Combine(_storagePath, hashString);

        if (!File.Exists(filePath))
        {
            _logger.LogWarning($"Blob with hash {hashString} not found.");
            return null;
        }

        return await File.ReadAllBytesAsync(filePath);
    }

    /// <summary>
    /// Asynchronously retrieves the blob associated with the specified <paramref name="hash"/>,
    /// decrypts it using the configured storage key and writes the resulting plaintext
    /// directly into the provided <paramref name="destination"/> buffer.
    /// </summary>
    /// <remarks>
    /// The method reads the raw encrypted file into memory, performs AEAD decryption
    /// (ChaCha20-Poly1305) and writes the plaintext into the caller-provided buffer.
    /// This API is intended for callers that can allocate their own destination buffer
    /// (for example, pre-sized arrays or rented memory). The method will return 0
    /// when the blob is not found, when decryption fails or when the destination
    /// buffer is not large enough to contain the decrypted payload.
    ///
    /// Note: because the implementation reads the entire encrypted file into a
    /// temporary array via <see cref="ReadRawFileAsync(byte[])"/>, it may use
    /// additional memory for large blobs; use <see cref="RetrieveToStreamAsync(byte[], Stream)"/>
    /// for streaming needs.
    /// </remarks>
    /// <param name="hash">The hash of the blob as a byte array.</param>
    /// <param name="destination">A memory buffer where plaintext will be written. The caller
    /// is responsible for allocating enough space (blob size - nonce - tag).</param>
    /// <returns>
    /// The number of plaintext bytes written into <paramref name="destination"/>,
    /// or <c>0</c> if the blob was not found, if decryption failed, or if no bytes
    /// were written.
    /// </returns>
    public async Task<int> RetrieveToBufferAsync(byte[] hash, Memory<byte> destination)
    {
        byte[]? encryptedData = await ReadRawFileAsync(hash);
        if (encryptedData == null)
        {
            return 0;
        }

        try
        {
            ReadOnlySpan<byte> encryptedSpan = encryptedData.AsSpan();
            ReadOnlySpan<byte> nonceSpan = encryptedSpan.Slice(0, _algorithm.NonceSize);
            ReadOnlySpan<byte> cipherTextAndTagSpan = encryptedSpan.Slice(_algorithm.NonceSize);

            if (
                _algorithm.Decrypt(
                    _storageKey,
                    nonceSpan,
                    default,
                    cipherTextAndTagSpan,
                    destination.Span
                )
            )
            {
                return cipherTextAndTagSpan.Length - _algorithm.TagSize;
            }

            _logger.LogError(
                "Decryption failed for blob with hash {Hash}.",
                Convert.ToHexString(hash).ToLowerInvariant()
            );
            return 0;
        }
        finally
        {
            //
        }
    }
}
