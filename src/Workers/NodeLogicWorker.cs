using System.Buffers;
using System.Net;
using System.Threading.Channels;
using FalconNode.Core.Messages;
using NSec.Cryptography;

namespace FalconNode.Workers;

/// <summary>
/// Worker responsible for processing incoming network packets and implementing node logic.
/// </summary>
public class NodeLogicWorker : BackgroundService
{
    private readonly ChannelReader<NetworkPacket> _incomingReader;

    private readonly Key _myIdentityKey = Key.Create(SignatureAlgorithm.Ed25519);
    private readonly Key _myOnionKey = Key.Create(KeyAgreementAlgorithm.X25519);

    private readonly ILogger<NodeLogicWorker> _logger;

    public NodeLogicWorker(Channel<NetworkPacket> channel, ILogger<NodeLogicWorker> logger)
    {
        _incomingReader = channel.Reader;
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
            $"Processing packet of type {packet.MessageType} received at {packet.Timestamp}"
        );

        var result = packet.MessageType switch
        {
            0x01 => this.HandleHandshake(packet),
            0x02 => this.HandleDhtLookup(packet),
            0x03 => this.HandleOnionRoute(packet),
            _ => this.HandleUnknown(packet),
        };
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
            Ed25519 algorithm = SignatureAlgorithm.Ed25519;
            PublicKey peerPublicKey = PublicKey.Import(
                algorithm,
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

            if (!algorithm.Verify(peerPublicKey, signableData, handshake.Signature))
            {
                _logger.LogWarning("Invalid handshake signature.");
                return false;
            }

            _logger.LogInformation("Handshake verified successfully.");
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
        byte[] signature = SignatureAlgorithm.Ed25519.Sign(_myIdentityKey, signableData);

        // 3. Serialize Payload
        byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(HandshakePayload.Size);
        var payload = new HandshakePayload(identityBytes, onionBytes, (ulong)now, signature);
        payload.WriteToSpan(payloadBuffer);

        // TODO: 4. Create Header + Payload

        return new OutgoingMessage(
            target,
            payloadBuffer.AsMemory(0, HandshakePayload.Size),
            payloadBuffer
        );
    }

    private bool HandleDhtLookup(NetworkPacket packet)
    {
        System.Console.WriteLine("Handling DHT Lookup packet.");
        return true;
    }

    private bool HandleOnionRoute(NetworkPacket packet)
    {
        System.Console.WriteLine("Handling Onion Route packet.");
        return true;
    }

    private bool HandleUnknown(NetworkPacket packet)
    {
        System.Console.WriteLine("Handling unknown packet.");
        return true;
    }
}
