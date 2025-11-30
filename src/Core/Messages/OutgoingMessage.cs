using System.Net;

namespace FalconNode.Core.Messages;

/// <summary>
/// Represents an outgoing network message, including its target endpoint, payload, and a reference to the original buffer.
/// </summary>
/// <param name="Target">The <see cref="IPEndPoint"/> representing the destination of the message.</param>
/// <param name="Payload">The message payload as a <see cref="Memory{Byte}"/>.</param>
/// <param name="OriginalBufferReference">A reference to the original byte buffer containing the message data. Used for buffer management and optimization.</param>
/// <summary>
/// Represents a message that is sent out from a node to another node in the network.
/// </summary>
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