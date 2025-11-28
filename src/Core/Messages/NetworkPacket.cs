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
    public readonly ulong Timestamp;
    public readonly uint RequestId;
    public readonly byte MessageType;
    public readonly Memory<byte> Payload;
    public readonly byte[] OriginalBufferReference;


    public NetworkPacket(
        byte messageType,
        uint requestId,
        Memory<byte> payload,
        byte[] originalBuffer
    )
    {
        MessageType = messageType;
        RequestId = requestId;
        Payload = payload;
        OriginalBufferReference = originalBuffer;
    }
}