using System.Buffers;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using FalconNode.Core.Dht;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
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
    /// The DHT service used for distributed hash table operations.
    /// </summary>
    private readonly DhtService _dhtService;

    /// <summary>
    /// The request manager for handling network requests and responses.
    /// </summary>
    private readonly RequestManager _requestManager;

    /// <summary>
    /// The node logic worker responsible for sending requests and processing responses.
    /// </summary>
    private readonly ChannelWriter<OutgoingMessage> _outWriter;

    /// <summary>
    /// The logger instance for logging file retrieval information.
    /// </summary>
    private readonly ILogger<FileRetriever> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileRetriever"/> class.
    /// </summary>
    /// <param name="blobStore">The blob store used for retrieving file chunks and manifests.</param>
    /// <param name="dhtService">The DHT service used for distributed hash table operations.</param>
    /// <param name="requestManager">The request manager for handling network requests and responses.</param>
    /// <param name="outWriter">The node logic worker responsible for sending requests and processing responses.</param>
    public FileRetriever(
        BlobStore blobStore,
        DhtService dhtService,
        RequestManager requestManager,
        ChannelWriter<OutgoingMessage> outWriter,
        ILogger<FileRetriever> logger
    )
    {
        _blobStore = blobStore;
        _dhtService = dhtService;
        _requestManager = requestManager;
        _outWriter = outWriter;
        _logger = logger;
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
    public async Task ReassembleFileAsync(string manifestHashString, Stream outputStream)
    {
        byte[] manifestHash = Convert.FromHexString(manifestHashString);

        // 1. Downloads the manifest
        // Use RetrieveBytesAsync because manifests are small and we need to parse them
        byte[]? manifestBytes = await _blobStore.RetrieveBytesAsync(manifestHash);
        if (manifestBytes == null)
        {
            throw new FileNotFoundException("Manifest not found in blob store", manifestHashString);
        }

        FileManifest? manifest = JsonSerializer.Deserialize(manifestBytes, FreedomNodeJsonContext.Default.FileManifest);
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
                _logger.LogInformation(
                    $"Chunk {chunkHashString} not found in blob store. Searching DHT..."
                );

                byte[]? chunkData = await GetBlobOrFetchAsync(chunkHash);

                if (chunkData == null)
                {
                    throw new FileNotFoundException(
                        $"Chunk {chunkHashString} not found in blob store."
                    );
                }

                await outputStream.WriteAsync(chunkData);
            }

            // Note: RetrieveToStreamAsync already wrote to outputStream,
            // so we don't need an explicit WriteAsync here.
        }
    }

    /// <summary>
    /// Attempts to retrieve a blob identified by the given hash. The method first checks the local blob store.
    /// If not found locally, it performs a DHT lookup to find potential holders and tries to fetch the blob from them.
    /// Successfully fetched blobs are cached locally before returning.
    /// </summary>
    /// <param name="hash">The hash identifying the blob to retrieve.</param>
    /// <returns>
    /// A byte array containing the blob data if found; otherwise, <c>null</c>.
    /// </returns>
    private async Task<byte[]?> GetBlobOrFetchAsync(byte[] hash)
    {
        // 1. Check local blob store
        byte[]? localData = await _blobStore.RetrieveBytesAsync(hash);

        if (localData != null)
        {
            return localData;
        }

        // 2. DHT Lookup
        NodeId targetId = new NodeId(hash);
        List<Contact> potentialHolders = await _dhtService.LookupAsync(targetId);

        _logger.LogInformation($"DHT found {potentialHolders.Count} potential holders for blob.");

        // 3. Try to request the blob from each contact (ideally in parallel, but sequential for simplicity)
        foreach (var contact in potentialHolders)
        {
            try
            {
                byte[]? data = await FetchFromNodeAsync(contact.Endpoint, hash);
                if (data != null)
                {
                    await _blobStore.StoreAsync(data); // Cache locally
                    return data;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to fetch blob from {contact.Endpoint}: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Asynchronously sends a request to a remote node to fetch a file by its hash and returns the file data as a byte array.
    /// </summary>
    /// <param name="target">The <see cref="IPEndPoint"/> of the target node to send the request to.</param>
    /// <param name="hash">A 32-byte array representing the hash of the file to retrieve.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the file data as a byte array.</returns>
    /// <exception cref="Exception">Thrown if the response message type is invalid.</exception>
    private async Task<byte[]> FetchFromNodeAsync(IPEndPoint target, byte[] hash)
    {
        uint requestId = _requestManager.NextId();

        int packetSize = FixedHeader.Size + 32;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(packetSize);

        try
        {
            hash.CopyTo(buffer.AsSpan(FixedHeader.Size));

            Span<byte> payloadSpan = buffer.AsSpan(FixedHeader.Size, 32);

            // Create Header with CRC32
            FixedHeader header = FixedHeader.Create(0x07, requestId, payloadSpan);

            // Write Header
            header.WriteToSpan(buffer.AsSpan(0, FixedHeader.Size));

            Task<NetworkPacket> task = _requestManager.RegisterRequestAsync(
                requestId,
                TimeSpan.FromSeconds(5)
            );

            await _outWriter.WriteAsync(
                new OutgoingMessage(target, buffer.AsMemory(0, packetSize), buffer)
            );

            NetworkPacket response = await task;

            // 0x08 = FETCH_RES
            if (response.MessageType == 0x08)
            {
                return response.Payload.ToArray();
            }

            throw new Exception("Invalid response message type");
        }
        catch
        {
            // Important: Return buffer to pool if something fails before sending
            // Note: If write succeeds, OutgoingMessage handles the buffer return?
            // In your current architecture, the ConnectionManagerWorker seems to handle return after send.
            // But if we throw here (e.g. RegisterRequestAsync fails), we must return it.
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }
}
