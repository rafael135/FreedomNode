using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using FalconNode.Core.Storage;

namespace FalconNode.Core.FS;

/// <summary>
/// Provides functionality to ingest files by splitting them into chunks, storing each chunk in a blob store,
/// and saving a manifest describing the file and its chunks.
/// </summary>
public class FileIngestor
{
    /// <summary>
    /// The blob store used for storing file chunks and manifests.
    /// </summary>
    private readonly BlobStore _blobStore;
    /// <summary>
    /// The size of each chunk to be read from the source stream.
    /// </summary>
    private const int ChunkSize = 256 * 1024; // 256 KB

    /// <summary>
    /// Initializes a new instance of the <see cref="FileIngestor"/> class.
    /// </summary>
    /// <param name="blobStore">The blob store used for storing file chunks and manifests.</param>
    public FileIngestor(BlobStore blobStore)
    {
        _blobStore = blobStore;
    }

    /// <summary>
    /// Asynchronously ingests a file from the provided <paramref name="sourceStream"/>, splits it into chunks,
    /// stores each chunk in the blob store, and saves a manifest describing the file and its chunks.
    /// </summary>
    /// <param name="sourceStream">The stream containing the file data to ingest.</param>
    /// <param name="fileName">The name of the file being ingested.</param>
    /// <param name="contentType">The MIME type of the file.</param>
    /// <returns>
    /// A <see cref="Task{String}"/> representing the asynchronous operation, with the result being
    /// the hexadecimal hash string of the stored manifest.
    /// </returns>
    /// <remarks>
    /// The method reads the file in chunks, stores each chunk, and returns a manifest hash.
    /// The buffer used for chunking is rented from the shared array pool and returned after use.
    /// </remarks>
    public async Task<string> IngestAsync(Stream sourceStream, string fileName, string contentType)
    {
        var manifest = new FileManifest
        {
            FileName = fileName,
            ContentType = contentType,
            TotalSize = sourceStream.Length,
        };

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            while (true)
            {
                // 1. Read from stream
                int bytesRead = await sourceStream.ReadAtLeastAsync(
                    buffer.AsMemory(0, ChunkSize),
                    ChunkSize,
                    throwOnEndOfStream: false
                );

                // If no bytes were read, we are done
                if (bytesRead == 0)
                {
                    break;
                }

                // 2. Isolate the exact chunk readed
                // If the file has 300Kb
                // Loop 1: Reads 256Kb. Slice 256Kb
                // Loop 2: Reads 44Kb. Slice 44Kb
                // OPTIMIZATION: Created a Slice (struct on stack), no new array allocation
                ReadOnlyMemory<byte> chunkSlice = buffer.AsMemory(0, bytesRead);

                // 3. Save the chunk to the blob store
                // Optimization: Now StoreAsync accepts ReadOnlyMemory<byte>, so we don't need .ToArray()
                byte[] chunkHash = await _blobStore.StoreAsync(chunkSlice);

                manifest.Chunks.Add(Convert.ToHexString(chunkHash).ToLowerInvariant());
            }
        }
        finally
        {
            // Critical: Return the buffer to the pool
            // This ensures we don't leak memory by not returning the rented buffer
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // 4. Saves the manifest to the blob store
        // Note: The json serialization is temporary and can be optimized later
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        // manifestBytes is small, so treating it as Memory is cheap
        byte[] manifestHash = await _blobStore.StoreAsync(manifestBytes);

        return Convert.ToHexString(manifestHash).ToLowerInvariant();
    }
}
