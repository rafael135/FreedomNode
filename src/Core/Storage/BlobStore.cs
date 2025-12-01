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
    /// Stores the given binary data as a blob in the storage directory using efficient memory handling.
    /// The blob is identified by its SHA-256 hash, which is used as the filename.
    /// If a blob with the same hash already exists, the method skips storage.
    /// The write operation is performed atomically using a temporary file to prevent incomplete writes.
    /// </summary>
    /// <param name="data">The binary data to store (as a read-only memory region).</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation,
    /// with the SHA-256 hash of the stored data as a byte array.
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
            await fs.WriteAsync(data);
        }

        try
        {
            File.Move(tempPath, filePath, overwrite: true);
            _logger.LogInformation(
                $"Stored blob with hash {hashString} at {filePath}, with size {data.Length} bytes."
            );
        }
        catch (IOException)
        {
            // Race condition: Someone stored the same blob milliseconds ago. Ignore.
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
        string hashString = Convert.ToHexString(hash).ToLowerInvariant();
        string filePath = Path.Combine(_storagePath, hashString);

        if (!File.Exists(filePath))
            return false;

        await using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true
        );
        await fs.CopyToAsync(targetStream);

        return true;
    }

    /// <summary>
    /// Asynchronously retrieves the blob data associated with the specified hash.
    /// Use this only for small blobs (like Manifests) to avoid high memory usage.
    /// </summary>
    /// <param name="hash">The hash of the blob to retrieve.</param>
    /// <returns>
    /// A byte array containing the blob data if found; otherwise, <c>null</c>.
    /// </returns>
    /// <remarks>
    /// Logs a warning if the blob is not found, and logs information upon successful retrieval.
    /// </remarks>
    public async Task<byte[]?> RetrieveBytesAsync(byte[] hash)
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

    /// <summary>
    /// Gets the size of the blob associated with the specified hash.
    /// </summary>
    /// <param name="hash">The hash of the blob as a byte array.</param>
    /// <returns></returns>
    public long? GetBlobSize(byte[] hash)
    {
        string hashString = Convert.ToHexString(hash).ToLowerInvariant();
        string filePath = Path.Combine(_storagePath, hashString);

        FileInfo info = new FileInfo(filePath);
        return info.Exists ? info.Length : null;
    }

    /// <summary>
    /// Asynchronously retrieves the blob data associated with the specified hash
    /// and writes it directly into the provided memory buffer.
    /// </summary>
    /// <param name="hash">The hash of the blob as a byte array.</param>
    /// <param name="destination">The memory buffer to write the blob data into.</param>
    /// <returns>The number of bytes read into the buffer.</returns>
    public async Task<int> RetrieveToBufferAsync(byte[] hash, Memory<byte> destination)
    {
        string hashString = Convert.ToHexString(hash).ToLowerInvariant();
        string filePath = Path.Combine(_storagePath, hashString);

        if (!File.Exists(filePath))
        {
            return 0;
        }

        await using FileStream fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true
        );

        // Reads directly into the provided Memory<byte> destination
        int bytesRead = await fs.ReadAsync(destination);

        return bytesRead;
    }
}
