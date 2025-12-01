using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using FalconNode.Core.Crypto;
using FalconNode.Core.Dht;
using FalconNode.Core.FS;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
using FalconNode.Core.State;
using FalconNode.Core.Storage;
using NSec.Cryptography;

namespace FalconNode.Workers;

/// <summary>
/// The <c>NodeLogicWorker</c> class is responsible for handling the core logic of a network node,
/// including processing incoming network packets, managing peer and routing tables, storing and retrieving blobs,
/// ingesting files, and handling cryptographic operations for identity and onion routing.
/// It operates as a background service, reading packets from an incoming channel, processing them asynchronously,
/// and writing responses or outgoing messages to an outgoing channel.
/// The worker supports handshake authentication, onion routing, DHT node lookups, blob storage and retrieval,
/// and message publishing. It optimizes memory usage by leveraging buffer pooling and direct memory operations,
/// and provides detailed logging for operational visibility and error handling.
/// </summary>
public class NodeLogicWorker : BackgroundService
{
    /// <summary>
    /// Channel reader for incoming network packets.
    /// </summary>
    private readonly ChannelReader<NetworkPacket> _incomingReader;

    /// <summary>
    /// Channel writer for outgoing messages.
    /// </summary>
    private readonly ChannelWriter<OutgoingMessage> _outgoingWriter;

    /// <summary>
    /// The peer table for managing known peers.
    /// </summary>
    private readonly PeerTable _peerTable;

    /// <summary>
    /// The routing table for managing network routes.
    /// </summary>
    private readonly RoutingTable _routingTable;

    /// <summary>
    /// The blob store for storing and retrieving data blobs.
    /// </summary>
    private readonly BlobStore _blobStore;

    /// <summary>
    /// The file ingestor for processing and ingesting files.
    /// </summary>
    private readonly FileIngestor _fileIngestor;

    /// <summary>
    /// The identity and onion keys for the node.
    /// </summary>
    // Static algorithms to avoid reallocation overhead
    private static readonly SignatureAlgorithm _ed25519 = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// The X25519 key agreement algorithm for onion routing.
    /// </summary>
    private static readonly KeyAgreementAlgorithm _x25519 = KeyAgreementAlgorithm.X25519;

    /// <summary>
    /// The node's identity key used for signing and verification.
    /// </summary>
    private readonly Key _myIdentityKey = Key.Create(_ed25519);

    /// <summary>
    /// The node's onion key used for onion routing.
    /// </summary>
    private readonly Key _myOnionKey = Key.Create(_x25519);

    /// <summary>
    /// Logger for logging information and errors.
    /// </summary>
    private readonly ILogger<NodeLogicWorker> _logger;

    public NodeLogicWorker(
        Channel<NetworkPacket> channel,
        Channel<OutgoingMessage> outgoingChannel,
        PeerTable peerTable,
        RoutingTable routingTable,
        BlobStore blobStore,
        FileIngestor fileIngestor,
        ILogger<NodeLogicWorker> logger
    )
    {
        _incomingReader = channel.Reader;
        _outgoingWriter = outgoingChannel.Writer;
        _peerTable = peerTable;
        _routingTable = routingTable;
        _blobStore = blobStore;
        _fileIngestor = fileIngestor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Node Logic Worker started.");

        await foreach (var packet in _incomingReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Updated to await the processing, ensuring async operations complete
                await ProcessPacket(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing packet.");
            }
            finally
            {
                // Return the original buffer to the pool to avoid memory leaks
                ArrayPool<byte>.Shared.Return(packet.OriginalBufferReference);
            }
        }
    }

    /// <summary>
    /// Processes an incoming network packet based on its message type.
    /// </summary>
    /// <param name="packet">The network packet to process.</param>
    private async Task ProcessPacket(NetworkPacket packet)
    {
        _logger.LogInformation(
            $"Processing packet of type {packet.MessageType} received from {packet.Origin}"
        );

        switch (packet.MessageType)
        {
            case 0x01:
                HandleHandshake(packet);
                break;
            case 0x02:
                HandleOnionRoute(packet);
                break;
            case 0x03:
                HandleDhtLookup(packet);
                break;
            case 0x05:
                await HandleStore(packet);
                break;
            case 0x09:
                // 0x09 (Ping) handler removed — example moved to CONTRIBUTING.md as documentation-only
                // To implement: add a handler like the repository examples (FixedHeader, ArrayPool, OutgoingMessage)
                break;
            case 0x07:
                await HandleFetch(packet);
                break;
            default:
                HandleUnknown(packet);
                break;
        }
    }

    /// <summary>
    /// Handles the storage of a network packet's payload by passing it to the blob store,
    /// then responds to the origin with a confirmation and the resulting hash.
    /// Optimizes memory usage by passing <see cref="ReadOnlyMemory{T}"/> directly to <c>StoreAsync</c>.
    /// Logs the operation and handles any exceptions that occur during the process.
    /// </summary>
    /// <param name="packet">The <see cref="NetworkPacket"/> containing the payload to store and metadata for response.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task HandleStore(NetworkPacket packet)
    {
        try
        {
            // 1. Extract data
            // Optimization: Pass ReadOnlyMemory directly to StoreAsync.
            // Avoids .ToArray() allocation (copying the whole file to Heap).
            byte[] hash = await _blobStore.StoreAsync(packet.Payload);

            // 2. Responds with "OK" and hash (STORE_RES - 0x06)
            int responsePayloadSize = 32; // Hash size
            int totalSize = FixedHeader.Size + responsePayloadSize;

            byte[] responseBuffer = ArrayPool<byte>.Shared.Rent(totalSize);

            // Write Header
            new FixedHeader(0x06, packet.RequestId, (uint)responsePayloadSize).WriteToSpan(
                responseBuffer.AsSpan(0, FixedHeader.Size)
            );

            // Write Payload (Hash)
            hash.CopyTo(responseBuffer.AsSpan(FixedHeader.Size));

            // 3. Send Response
            _outgoingWriter.TryWrite(
                new OutgoingMessage(
                    packet.Origin,
                    responseBuffer.AsMemory(0, totalSize),
                    responseBuffer
                )
            );

            _logger.LogInformation(
                $"Stored blob {Convert.ToHexString(hash)[..8]} from {packet.Origin}"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Store failed.");
        }
    }

    /// <summary>
    /// Handles a fetch request for a blob identified by its hash from the incoming <paramref name="packet"/>.
    /// Validates the payload, checks for blob existence and size, and streams the blob data back to the requester.
    /// If the blob is not found or is too large, logs a warning and does not send a response.
    /// Optimizes memory usage by renting a buffer and writing blob data directly into it.
    /// Ensures proper buffer management and error logging.
    /// </summary>
    /// <param name="packet">The network packet containing the fetch request and payload.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task HandleFetch(NetworkPacket packet)
    {
        try
        {
            if (packet.Payload.Length < 32)
                return;

            // 1. Get requested hash
            byte[] requestedHash = packet.Payload.Slice(0, 32).ToArray();

            // 2. Critical: Get only the size first
            long? blobSize = _blobStore.GetBlobSize(requestedHash);

            if (blobSize == null)
            {
                _logger.LogWarning(
                    $"FETCH: Blob not found {Convert.ToHexString(requestedHash)[..8]}"
                );
                // TODO: Send NOT_FOUND (0x404?)
                return;
            }

            // If the blob is too large, we should implement a streaming mechanism
            // TODO: Handle large blobs (streaming)
            if (blobSize > 10 * 1024 * 1024)
            {
                _logger.LogWarning($"FETCH: Blob too large for single packet.");
                return;
            }

            int dataLen = (int)blobSize.Value;
            int totalSize = FixedHeader.Size + dataLen;

            // 3. Rent buffer for response
            byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(totalSize);

            try
            {
                // 4. Write Header
                new FixedHeader(0x08, packet.RequestId, (uint)dataLen).WriteToSpan(
                    packetBuffer.AsSpan(0, FixedHeader.Size)
                );

                // 5. Retrieve blob data directly into the response buffer
                // Optimization: Avoids extra allocation by writing directly to the rented buffer
                Memory<byte> dataWindow = packetBuffer.AsMemory(FixedHeader.Size, dataLen);

                // Retrieve the blob data into the allocated window
                int bytesRead = await _blobStore.RetrieveToBufferAsync(requestedHash, dataWindow);

                if (bytesRead != dataLen)
                {
                    _logger.LogError(
                        $"FETCH: Expected {dataLen} bytes but read {bytesRead} bytes."
                    );
                    return;
                }

                // 6. Send Response
                _outgoingWriter.TryWrite(
                    new OutgoingMessage(
                        packet.Origin,
                        packetBuffer.AsMemory(0, totalSize),
                        packetBuffer
                    )
                );
            }
            catch
            {
                // In case of error, ensure we return the rented buffer
                // In case of success, the ConnectionManager will return it after sending
                ArrayPool<byte>.Shared.Return(packetBuffer);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fetch failed.");
        }
    }

    /// <summary>
    /// Publishes a message by ingesting the provided text content as a file,
    /// chunking and storing it locally, and returning the generated message identifier.
    /// </summary>
    /// <param name="textContent">The text content to be published.</param>
    /// <returns>
    /// A <see cref="Task{String}"/> representing the asynchronous operation,
    /// with the message identifier (hash) as the result.
    /// </returns>
    public async Task<string> PublishMessageAsync(string textContent)
    {
        // 1. Convert text to stream
        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(textContent));

        // 2. Ingest the file (this will create chunks + 1 manifest)
        // The ingestor handles chunking and storage locally
        string messageId = await _fileIngestor.IngestAsync(stream, "post.txt", "text/plain");

        // This is the hash propagated in the DHT
        return messageId;
    }

    /// <summary>
    /// Handles an incoming handshake packet, verifying its authenticity and integrity.
    /// </summary>
    /// <param name="packet">The network packet containing the handshake payload.</param>
    /// <returns>True if the handshake is valid; otherwise, false.</returns>
    private bool HandleHandshake(NetworkPacket packet)
    {
        if (packet.Payload.Length != HandshakePayload.Size)
        {
            _logger.LogWarning("Invalid handshake payload size.");
            return false;
        }

        Span<byte> payloadSpan = packet.Payload.Span;
        HandshakePayload handshake = HandshakePayload.ReadFromSpan(payloadSpan);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - (long)handshake.Timestamp) > 60000) // 1 minute tolerance
        {
            _logger.LogWarning("Handshake timestamp is too far from current time.");
            return false;
        }

        try
        {
            PublicKey peerPublicKey = PublicKey.Import(
                _ed25519,
                handshake.IdentityKey,
                KeyBlobFormat.RawPublicKey
            );

            Span<byte> signableData = stackalloc byte[72];
            HandshakePayload.WriteSignableBytes(
                signableData,
                handshake.IdentityKey,
                handshake.OnionKey,
                handshake.Timestamp
            );

            if (!_ed25519.Verify(peerPublicKey, signableData, handshake.Signature))
            {
                _logger.LogWarning("Invalid handshake signature.");
                return false;
            }

            // Register the peer as authenticated
            // We store its onion key for future encrypted communications
            _peerTable.RegisterAuthenticatedPeer(packet.Origin, handshake.OnionKey);

            _logger.LogInformation(
                $"Handshake verified successfully. Peer {packet.Origin} registered."
            );
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying handshake signature.");
            return false;
        }
    }

    /// <summary>
    /// Creates an outgoing handshake message to initiate a handshake with a target peer.
    /// </summary>
    /// <param name="target">The target endpoint to send the handshake message to.</param>
    /// <returns>An outgoing message containing the handshake payload.</returns>
    public OutgoingMessage CreateHandshakeMessage(IPEndPoint target)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 1. Prepare Data
        byte[] identityBytes = _myIdentityKey.Export(KeyBlobFormat.RawPublicKey);
        byte[] onionBytes = _myOnionKey.Export(KeyBlobFormat.RawPublicKey);

        // 2. Sign
        Span<byte> signableData = stackalloc byte[72];
        HandshakePayload.WriteSignableBytes(signableData, identityBytes, onionBytes, (ulong)now);
        byte[] signature = _ed25519.Sign(_myIdentityKey, signableData);

        // 3. Build Packet
        // Calculate total size and rent buffer
        int totalSize = FixedHeader.Size + HandshakePayload.Size;
        byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(totalSize);

        // Write the Header
        var header = new FixedHeader(0x01, 0, (uint)HandshakePayload.Size); // 0x01 = Handshake Type
        header.WriteToSpan(packetBuffer.AsSpan(0, FixedHeader.Size));

        // Write the Payload
        var payload = new HandshakePayload(identityBytes, onionBytes, (ulong)now, signature);
        payload.WriteToSpan(packetBuffer.AsSpan(FixedHeader.Size));

        return new OutgoingMessage(target, packetBuffer.AsMemory(0, totalSize), packetBuffer);
    }

    /// <summary>
    /// Handles an incoming DHT FindNode request packet.
    /// Validates the packet, updates the routing table with the origin contact,
    /// finds the closest nodes to the requested target ID, builds a response,
    /// and sends it back to the requester.
    /// </summary>
    /// <param name="packet">The incoming network packet containing the FindNode request.</param>
    /// <returns>
    /// <c>true</c> if the request was handled and a response was sent;
    /// <c>false</c> if the packet was invalid or could not be processed.
    /// </returns>
    private bool HandleDhtLookup(NetworkPacket packet)
    {
        if (packet.Payload.Length < 32)
            return false;

        var request = FindNodeRequest.ReadFromSpan(packet.Payload.Span);

        _logger.LogInformation($"DHT: Received FindNode request for NodeId: {request.TargetId}");

        if (_peerTable.TryGetPeerKey(packet.Origin, out var originKey))
        {
            // Example of deriving NodeId from peer's onion key (in real app, use Hash)
            var originNodeId = new NodeId(SHA256.HashData(originKey));
            _routingTable.AddContact(new Contact(originNodeId, packet.Origin));
        }

        // Find closest nodes to the target on our table
        List<Contact> closestNodes = _routingTable.FindClosest(request.TargetId);

        // Build the response
        FindNodeResponse response = new FindNodeResponse(closestNodes);
        int responseSize = response.CalculateSize();

        // Header + Payload
        byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(FixedHeader.Size + responseSize);

        // Header (0x04 = FIND_NODE_RES)
        var header = new FixedHeader(0x04, packet.RequestId, (uint)responseSize);
        header.WriteToSpan(packetBuffer.AsSpan(0, FixedHeader.Size));

        // Payload
        response.WriteToSpan(packetBuffer.AsSpan(FixedHeader.Size));

        // Send response back to requester
        OutgoingMessage outgoingMessage = new OutgoingMessage(
            packet.Origin,
            packetBuffer.AsMemory(0, FixedHeader.Size + responseSize),
            packetBuffer
        );

        _outgoingWriter.TryWrite(outgoingMessage);

        return true;
    }

    /// <summary>
    /// Handles the decryption and processing of an onion route packet.
    /// Extracts the sender's ephemeral public key and the encrypted onion layer from the packet payload.
    /// Performs key agreement to derive a session key, then attempts to decrypt the onion layer.
    /// If decryption is successful, processes the peeled layer; otherwise, logs a warning.
    /// </summary>
    /// <param name="packet">The <see cref="NetworkPacket"/> containing the onion route payload.</param>
    /// <returns>
    /// <c>true</c> if the onion layer was successfully decrypted and processed; <c>false</c> otherwise.
    /// </returns>
    private bool HandleOnionRoute(NetworkPacket packet)
    {
        var payloadSpan = packet.Payload.Span;

        // 32 Bytes for the key agreement public key + 28 Bytes for Ciphertext overhead
        if (payloadSpan.Length < 32 + 28)
        {
            return false;
        }

        var senderEphemeralKeyBytes = payloadSpan.Slice(0, 32);
        var encryptedOnionLayer = payloadSpan.Slice(32);

        try
        {
            PublicKey senderPublicKey = PublicKey.Import(
                _x25519,
                senderEphemeralKeyBytes,
                KeyBlobFormat.RawPublicKey
            );
            using Key sessionKey = CryptoHelper.CreateSessionKey(_myOnionKey, senderPublicKey);

            // The encryptedOnionLayer does not have the 32-byte key.
            // So we subtract only the overhead (28 bytes) FROM IT.
            int plainTextSize = encryptedOnionLayer.Length - 28;

            if (plainTextSize <= 0)
                return false;

            byte[] peeledLayer = ArrayPool<byte>.Shared.Rent(plainTextSize);

            try
            {
                if (
                    CryptoHelper.TryDecryptLayer(
                        sessionKey,
                        encryptedOnionLayer, // Use correct slice
                        peeledLayer.AsSpan(0, plainTextSize)
                    )
                )
                {
                    ProcessPeeledLayer(peeledLayer.AsSpan(0, plainTextSize));
                    return true;
                }
                else
                {
                    _logger.LogWarning("Failed to decrypt onion layer.");
                    return false;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(peeledLayer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing onion route packet.");
            return false;
        }
    }

    /// <summary>
    /// Processes a peeled layer of an onion-routed message. Determines whether the message is intended for the final destination
    /// or should be relayed to the next hop. If the message is for the final destination, logs the message length and provides a
    /// placeholder for further processing. If the message is to be relayed, extracts the next hop's IP address and port, constructs
    /// the appropriate header, and prepares the outgoing message for transmission.
    /// </summary>
    /// <param name="data">
    /// The span of bytes representing the peeled onion layer, including the command byte, address information, and payload.
    /// </param>
    private void ProcessPeeledLayer(Span<byte> data)
    {
        byte command = data[0];

        if (command == 0x00) // Final Destination
        {
            Span<byte> finalContent = data.Slice(1);
            _logger.LogInformation(
                $"Received final onion message! Message length: {finalContent.Length} bytes."
            );

            // TODO: Process final content (Store in 'DB' / Show in UI)
        }
        else if (command == 0x01) // Relay (next hop)
        {
            int ipLen = data[1];
            Span<byte> ipBytes = data.Slice(2, ipLen);
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2 + ipLen, 2));

            // CORRECTION: Ensure we use an array for IPAddress constructor compatibility
            IPAddress nextHopIp = new IPAddress(ipBytes.ToArray());
            IPEndPoint nextHopEndpoint = new IPEndPoint(nextHopIp, port);

            Span<byte> nextPayload = data.Slice(2 + ipLen + 2);

            _logger.LogInformation(
                $"Relaying onion message to {nextHopEndpoint} with payload size {nextPayload.Length} bytes."
            );

            // We need to add the Header before sending, or the next node will reject it.
            // Total size = Header + Remaining Onion Payload
            int totalPacketSize = FixedHeader.Size + nextPayload.Length;
            byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(totalPacketSize);

            // Build Onion Data Header (0x02)
            var header = new FixedHeader(0x02, 0, (uint)nextPayload.Length);
            header.WriteToSpan(packetBuffer.AsSpan(0, FixedHeader.Size));

            // Copy the remaining payload
            nextPayload.CopyTo(packetBuffer.AsSpan(FixedHeader.Size));

            OutgoingMessage outgoingMessage = new OutgoingMessage(
                nextHopEndpoint,
                packetBuffer.AsMemory(0, totalPacketSize),
                packetBuffer
            );

            _outgoingWriter.TryWrite(outgoingMessage);
        }
    }

    private bool HandleUnknown(NetworkPacket packet)
    {
        _logger.LogWarning($"Received unknown packet type: {packet.MessageType}");
        return true;
    }

    // Ping handler removed — see CONTRIBUTING.md for a full commented example to add this behavior.
}
