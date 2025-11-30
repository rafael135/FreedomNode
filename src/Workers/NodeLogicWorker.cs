using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using FalconNode.Core.Crypto;
using FalconNode.Core.Dht;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
using FalconNode.Core.State;
using FalconNode.Core.Storage;
using NSec.Cryptography;

namespace FalconNode.Workers;

/// <summary>
/// Worker responsible for processing incoming network packets and implementing node logic.
/// </summary>
public class NodeLogicWorker : BackgroundService
{
    private readonly ChannelReader<NetworkPacket> _incomingReader;
    private readonly ChannelWriter<OutgoingMessage> _outgoingWriter;
    private readonly PeerTable _peerTable;
    private readonly RoutingTable _routingTable;

    private readonly BlobStore _blobStore;

    // Static algorithms to avoid reallocation overhead
    private static readonly SignatureAlgorithm _ed25519 = SignatureAlgorithm.Ed25519;
    private static readonly KeyAgreementAlgorithm _x25519 = KeyAgreementAlgorithm.X25519;

    private readonly Key _myIdentityKey = Key.Create(_ed25519);
    private readonly Key _myOnionKey = Key.Create(_x25519);

    private readonly ILogger<NodeLogicWorker> _logger;

    public NodeLogicWorker(
        Channel<NetworkPacket> channel,
        Channel<OutgoingMessage> outgoingChannel,
        PeerTable peerTable,
        RoutingTable routingTable,
        ILogger<NodeLogicWorker> logger
    )
    {
        _incomingReader = channel.Reader;
        _outgoingWriter = outgoingChannel.Writer;
        _peerTable = peerTable;
        _routingTable = routingTable;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Node Logic Worker started.");

        await foreach (var packet in _incomingReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                ProcessPacket(packet);
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
    private void ProcessPacket(NetworkPacket packet)
    {
        _logger.LogInformation(
            $"Processing packet of type {packet.MessageType} received from {packet.Origin}"
        );

        // Discard result using '_' to satisfy compiler / analyzer
        _ = packet.MessageType switch
        {
            0x01 => this.HandleHandshake(packet),
            0x02 => this.HandleOnionRoute(packet),
            0x03 => this.HandleDhtLookup(packet),
            _ => this.HandleUnknown(packet),
        };
    }

    private async void HandleStore(NetworkPacket packet)
    {
        try
        {
            // 1. Extract data
            byte[] dataToStore = packet.Payload.ToArray();

            // 2. Persists data
            byte[] hash = await _blobStore.StoreAsync(dataToStore);

            // 3. Responds with "OK" and hash
            int responseSize = 32;
            byte[] responseBuffer = ArrayPool<byte>.Shared.Rent(FixedHeader.Size + responseSize);

            new FixedHeader(0x06, packet.RequestId, (uint)responseSize).WriteToSpan(
                responseBuffer.AsSpan(0, FixedHeader.Size)
            );

            hash.CopyTo(responseBuffer.AsSpan(0, FixedHeader.Size));

            _outgoingWriter.TryWrite(
                new OutgoingMessage(
                    packet.Origin,
                    responseBuffer.AsMemory(0, FixedHeader.Size + responseSize),
                    responseBuffer
                )
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Store failed.");
        }
    }

    private async void HandleFetch(NetworkPacket packet)
    {
        try
        {
            if (packet.Payload.Length < 32)
                return;

            byte[] requestedHash = packet.Payload.Slice(0, 32).ToArray();

            // 1. Try to retrieve data
            byte[]? data = await _blobStore.RetrieveAsync(requestedHash);

            if (data == null)
            {
                // TODO: Send NOT_FOUND (0x404?)
                return;
            }

            // 2. Send data
            // Header (0x08 = FETCH_RES)
            int totalSize = FixedHeader.Size + data.Length;
            byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(totalSize);

            data.CopyTo(packetBuffer.AsSpan(FixedHeader.Size));

            _outgoingWriter.TryWrite(
                new OutgoingMessage(
                    packet.Origin,
                    packetBuffer.AsMemory(0, totalSize),
                    packetBuffer
                )
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fetch failed.");
        }
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

    private bool HandleDhtLookup(NetworkPacket packet)
    {
        if (packet.Payload.Length < 32)
            return false;

        var request = FindNodeRequest.ReadFromSpan(packet.Payload.Span);

        _logger.LogInformation($"DHT: Received FindNode request for NodeId: {request.TargetId}");

        if (_peerTable.TryGetPeerKey(packet.Origin, out var originKey))
        {
            // Example of deriving NodeId from peer's onion key
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

            // The encryptedOnionLayer donst have não tem a chave de 32 bytes.
            // Then we subtract only the overhead (28 bytes) FROM IT, not from the total payload.
            int plainTextSize = encryptedOnionLayer.Length - 28;

            if (plainTextSize <= 0)
                return false;

            byte[] peeledLayer = ArrayPool<byte>.Shared.Rent(plainTextSize);

            try
            {
                if (
                    CryptoHelper.TryDecryptLayer(
                        sessionKey,
                        encryptedOnionLayer, // Usamos o slice correto
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

            IPAddress nextHopIp = new IPAddress(ipBytes);
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
}
