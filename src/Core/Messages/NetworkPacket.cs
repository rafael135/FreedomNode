using System.Net;

namespace FalconNode.Core.Messages;

/// <summary>
/// Represents an immutable network packet containing a timestamp, message type, and payload data.
/// </summary>
/// <remarks>
/// This structure is designed to encapsulate network message data with automatic timestamping.
/// The timestamp is automatically set to the current UTC time in milliseconds when the packet is created.
/// </remarks>
public readonly record struct NetworkPacket
{
    /// <summary>
    /// The origin endpoint of the packet.
    /// </summary>
    public readonly IPEndPoint Origin;

    /// <summary>
    /// The timestamp indicating when the packet was created, in milliseconds since Unix epoch.
    /// </summary>
    public readonly ulong Timestamp;

    /// <summary>
    /// The unique request ID associated with the packet.
    /// </summary>
    public readonly uint RequestId;

    /// <summary>
    /// The type of the message contained in the packet.
    /// </summary>
    public readonly byte MessageType;

    /// <summary>
    /// The payload data of the packet.
    /// </summary>
    public readonly Memory<byte> Payload;

    /// <summary>
    /// A reference to the original byte buffer containing the packet data.
    /// </summary>
    public readonly byte[] OriginalBufferReference;

    public NetworkPacket(
        IPEndPoint origin,
        byte messageType,
        uint requestId,
        Memory<byte> payload,
        byte[] originalBuffer
    )
    {
        Origin = origin;
        MessageType = messageType;
        RequestId = requestId;
        Payload = payload;
        OriginalBufferReference = originalBuffer;
    }
}
