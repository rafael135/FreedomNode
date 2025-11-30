using System.Security.Cryptography;

namespace FalconNode.Core.Storage;

/// <summary>
/// Provides functionality for storing and retrieving binary blobs on disk, identified by their SHA-256 hash.
/// Blobs are stored atomically to prevent incomplete writes, and duplicate blobs are not stored multiple times.
/// </summary>
/// <remarks>
/// The storage directory is initialized under the application's base directory at "data/blobs".
/// All operations are logged using the provided <see cref="ILogger{BlobStore}"/>.
/// </remarks>
/// <summary>
/// Provides storage and retrieval operations for binary large objects (blobs).
/// </summary>
public class BlobStore
{
    private readonly string _storagePath;
    private readonly ILogger<BlobStore> _logger;

    public BlobStore(ILogger<BlobStore> logger)
    {
        _storagePath = Path.Combine(AppContext.BaseDirectory, "data", "blobs");
        Directory.CreateDirectory(_storagePath);
        _logger = logger;
    }

    /// <summary>
    /// Stores the given binary data as a blob in the storage directory.
    /// The blob is identified by its SHA-256 hash, which is used as the filename.
    /// If a blob with the same hash already exists, the method skips storage.
    /// The write operation is performed atomically using a temporary file to prevent incomplete writes.
    /// </summary>
    /// <param name="data">The binary data to store.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation,
    /// with the SHA-256 hash of the stored data as a byte array.
    /// </returns>
    public async Task<byte[]> StoreAsync(byte[] data)
    {
        // 1. Calculate Hash (content id)
        byte[] hash = SHA256.HashData(data);
        string hashString = Convert.ToHexString(hash).ToLowerInvariant();
        string filePath = Path.Combine(_storagePath, hashString);

        // 2. If already exists, dont do anything
        if (File.Exists(filePath))
        {
            _logger.LogInformation(
                "Blob with hash {Hash} already exists. Skipping storage.",
                hashString
            );
            return hash;
        }

        // 3. Save to disk (Atomic, if possible)
        // The .tmp suffix ensures that incomplete writes are not mistaken for complete files if the node crashes during write.
        string tempPath = filePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, data);
        File.Move(tempPath, filePath);

        _logger.LogInformation(
            $"Stored blob with hash {hashString} at {filePath}, with size {data.Length} bytes."
        );
        return hash;
    }

    /// <summary>
    /// Asynchronously retrieves the blob data associated with the specified hash.
    /// </summary>
    /// <param name="hash">The hash of the blob to retrieve.</param>
    /// <returns>
    /// A byte array containing the blob data if found; otherwise, <c>null</c>.
    /// </returns>
    /// <remarks>
    /// Logs a warning if the blob is not found, and logs information upon successful retrieval.
    /// </remarks>
    public async Task<byte[]?> RetrieveAsync(byte[] hash)
    {
        string hashString = Convert.ToHexString(hash).ToLowerInvariant();
        string filePath = Path.Combine(_storagePath, hashString);

        if (!File.Exists(filePath))
        {
            _logger.LogWarning($"Blob with hash {hashString} not found.");
            return null;
        }

        byte[] data = await File.ReadAllBytesAsync(filePath);
        _logger.LogInformation(
            $"Retrieved blob with hash {hashString} from {filePath}, size {data.Length} bytes."
        );
        return data;
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
}
