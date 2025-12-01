using System.Text.Json;
using FalconNode.Core.Storage;

namespace FalconNode.Core.FS;

/// <summary>
/// Provides functionality to reassemble files from chunked storage using a manifest and a blob store.
/// </summary>
public class FileRetriever
{
    /// <summary>
    /// The blob store used for retrieving file chunks and manifests.
    /// </summary>
    private readonly BlobStore _blobStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRetriever"/> class.
    /// </summary>
    /// <param name="blobStore">The blob store used for retrieving file chunks and manifests.</param>
    public FileRetriever(BlobStore blobStore)
    {
        _blobStore = blobStore;
    }

    /// <summary>
    /// Reassembles a file from its manifest by sequentially retrieving and writing each chunk to the specified output stream.
    /// <para>
    /// The method first downloads and deserializes the file manifest using the provided manifest hash.
    /// It then iterates over the chunk hashes listed in the manifest, attempting to retrieve each chunk from the local blob store
    /// and write it directly to the output stream. If any chunk is missing, a <see cref="FileNotFoundException"/> is thrown.
    /// </para>
    /// <remarks>
    /// Future enhancements may include searching for missing chunks in the DHT network.
    /// </remarks>
    /// <param name="manifestHash">The hexadecimal string representing the hash of the file manifest.</param>
    /// <param name="outputStream">The stream to which the reassembled file will be written.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown if the manifest or any chunk listed in the manifest cannot be found in the blob store.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown if the manifest cannot be deserialized.
    /// </exception>
    /// </summary>
    public async Task ReassembleFileAsync(string manifestHash, Stream outputStream)
    {
        // 1. Downloads the manifest
        // Use RetrieveBytesAsync because manifests are small and we need to parse them
        byte[]? manifestBytes = await _blobStore.RetrieveBytesAsync(
            Convert.FromHexString(manifestHash)
        );
        if (manifestBytes == null)
        {
            throw new FileNotFoundException("Manifest not found in blob store", manifestHash);
        }

        var manifest = JsonSerializer.Deserialize<FileManifest>(manifestBytes);
        if (manifest == null)
        {
            throw new InvalidDataException("Failed to deserialize file manifest");
        }

        // 2. Iterate over chunks
        // TODO: Create a logic to search in the DHT network for missing chunks
        foreach (var chunkHashString in manifest.Chunks)
        {
            byte[] chunkHash = Convert.FromHexString(chunkHashString);

            // Tries to get from the local disk and pipe directly to outputStream
            // This avoids allocating memory for the chunk content
            bool found = await _blobStore.RetrieveToStreamAsync(chunkHash, outputStream);

            if (!found)
            {
                // TENSION: Implement DHT retrieval here
                // await _networkManager.FetchFromNetwork(chunkHash, outputStream);
                throw new FileNotFoundException(
                    $"Chunk {chunkHashString} not found in blob store."
                );
            }

            // Note: RetrieveToStreamAsync already wrote to outputStream,
            // so we don't need an explicit WriteAsync here.
        }
    }
}
