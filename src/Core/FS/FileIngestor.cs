using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using FalconNode.Core.Dht;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
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
    /// The DHT service used for distributed hash table operations.
    /// </summary>
    private readonly DhtService _dhtService;

    /// <summary>
    /// The node logic worker responsible for sending requests and processing responses.
    /// </summary>
    private readonly ChannelWriter<OutgoingMessage> _outWriter;

    private readonly ILogger<FileIngestor> _logger;

    /// <summary>
    /// The size of each chunk to be read from the source stream.
    /// </summary>
    private const int ChunkSize = 256 * 1024; // 256 KB

    /// <summary>
    /// The number of redundant copies to store for each chunk.
    /// </summary>
    private const int RedundancyCount = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileIngestor"/> class.
    /// </summary>
    /// <param name="blobStore">The blob store used for storing file chunks and manifests.</param>
    /// <param name="dhtService">The DHT service used for distributed hash table operations.</param>
    /// <param name="outWriter">The node logic worker responsible for sending requests and processing responses.</param>
    /// <param name="logger">The logger instance for logging file ingestion information.</param>
    public FileIngestor(
        BlobStore blobStore,
        DhtService dhtService,
        ChannelWriter<OutgoingMessage> outWriter,
        ILogger<FileIngestor> logger
    )
    {
        _blobStore = blobStore;
        _dhtService = dhtService;
        _outWriter = outWriter;
        _logger = logger;
    }

    /// <summary>
    /// Asynchronously ingests a file from the provided <paramref name="sourceStream"/>, splits it into chunks,
    /// stores each chunk in the blob store, propagates the chunks to the DHT for redundancy, and saves a manifest
    /// describing the file and its chunks. Returns the manifest hash as a lowercase hexadecimal string.
    /// </summary>
    /// <param name="sourceStream">The input stream containing the file data to ingest.</param>
    /// <param name="fileName">The name of the file being ingested.</param>
    /// <param name="contentType">The MIME type of the file.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation, with a result of the manifest hash
    /// as a lowercase hexadecimal string.
    /// </returns>
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
                // If the file has 300 KiB
                // Loop 1: Reads 256 KiB. Slice 256 KiB
                // Loop 2: Reads 44 KiB. Slice 44 KiB
                // OPTIMIZATION: Created a Slice (struct on stack), no new array allocation
                ReadOnlyMemory<byte> chunkSlice = buffer.AsMemory(0, bytesRead);

                // 3. Save the chunk to the blob store
                // Optimization: Now StoreAsync accepts ReadOnlyMemory<byte>, so we don't need .ToArray()
                byte[] chunkHash = await _blobStore.StoreAsync(chunkSlice);

                // 4. Propagate the chunk to the DHT for redundancy
                _ = PropagateChunkToDhtAsync(chunkHash, chunkSlice.ToArray());

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

        _ = PropagateChunkToDhtAsync(manifestHash, manifestBytes);

        return Convert.ToHexString(manifestHash).ToLowerInvariant();
    }

    /// <summary>
    /// Propagates a chunk of data to a set of neighboring nodes in the Distributed Hash Table (DHT).
    /// The method looks up nodes closest to the chunk's hash and sends the chunk data to a limited number of them,
    /// determined by <c>RedundancyCount</c>, to ensure redundancy.
    /// Logs the propagation result or any errors encountered during the process.
    /// </summary>
    /// <param name="chunkHash">The hash of the chunk to be propagated, used as the target key in the DHT lookup.</param>
    /// <param name="chunkData">The actual data of the chunk to be stored on neighboring nodes.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task PropagateChunkToDhtAsync(byte[] chunkHash, byte[] chunkData)
    {
        try
        {
            // Gets the neighboring nodes from the DHT
            NodeId targetId = new NodeId(chunkHash);
            List<Contact> nodes = await _dhtService.LookupAsync(targetId);

            IEnumerable<Contact> targets = nodes.Take(RedundancyCount);

            foreach (Contact node in targets)
            {
                // Send STORE (0x05)
                await SendStoreToNodeAsync(node.Endpoint, chunkData);
                _logger.LogInformation($"Chunk sent to {node.Endpoint}");
            }

            if (nodes.Count > 0)
            {
                _logger.LogInformation(
                    $"Propagated chunk {Convert.ToHexString(chunkHash)} to {targets.Count()} nodes in DHT."
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                $"Failed to propagate chunk {Convert.ToHexString(chunkHash)} to DHT."
            );
        }
    }

    /// <summary>
    /// Asynchronously sends a STORE message containing the specified data to the given node endpoint.
    /// The message is constructed with a fixed header and payload, and written to the outgoing writer.
    /// </summary>
    /// <param name="target">The <see cref="IPEndPoint"/> representing the target node to send the message to.</param>
    /// <param name="data">The byte array containing the data to be sent as the payload.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task SendStoreToNodeAsync(IPEndPoint target, byte[] data)
    {
        int packetSize = FixedHeader.Size + data.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(packetSize);

        // Header 0x05 (STORE)
        FixedHeader.Create(0x05, 0, data).WriteToSpan(buffer.AsSpan(0, FixedHeader.Size));

        // Payload
        data.CopyTo(buffer.AsSpan(FixedHeader.Size));

        await _outWriter.WriteAsync(
            new OutgoingMessage(target, buffer.AsMemory(0, packetSize), buffer)
        );
    }
}
