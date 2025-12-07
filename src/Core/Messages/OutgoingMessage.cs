using System.Net;

namespace FalconNode.Core.Messages;

/// <summary>
/// Represents an outgoing network message, including its target endpoint, payload, and a reference to the original buffer for pool management.
/// </summary>
/// <remarks>
/// This struct encapsulates all data required to send a message over the network, including the destination
/// endpoint and a reference to the pooled buffer that must be returned to <see cref="ArrayPool{T}.Shared"/>
/// after transmission completes.
/// </remarks>
public readonly record struct OutgoingMessage
{
    public readonly IPEndPoint Target;
    public readonly Memory<byte> Payload;
    public readonly byte[] OriginalBufferReference;

    public OutgoingMessage(IPEndPoint target, Memory<byte> payload, byte[] originalBufferReference)
    {
        Target = target;
        Payload = payload;
        OriginalBufferReference = originalBufferReference;
    }
}