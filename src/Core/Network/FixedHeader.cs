using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FalconNode.Core.Network;

/// <summary>
/// Represents the fixed header structure for network protocol messages.
/// </summary>
/// <remarks>
/// The <see cref="FixedHeader"/> struct is packed to 1 byte alignment and has a fixed size of 16 bytes.
/// It contains metadata for a network message, including version, flags, message type, reserved byte,
/// unique request ID, payload length, and CRC32 checksum.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct FixedHeader
{
    public const int Size = 16;

    public readonly byte Version; // 0x01
    public readonly byte Flags; // 0000 0000
    public readonly byte MessageType; // 0x01 - OnionData
    public readonly byte Reserved; // 0x00
    public readonly uint RequestId; // Unique ID for transaction
    public readonly uint PayloadLength; // Length of payload in bytes
    public readonly uint Checksum; // CRC32 checksum of payload

    public FixedHeader(byte type, uint reqId, uint payloadLength)
    {
        Version = 1;
        Flags = 0;
        MessageType = type;
        Reserved = 0;
        RequestId = reqId;
        PayloadLength = payloadLength;
        Checksum = 0; // Calculate later
    }

    private FixedHeader(
        byte version,
        byte flags,
        byte messageType,
        byte reserved,
        uint requestId,
        uint payloadLength,
        uint checksum
    )
    {
        Version = version;
        Flags = flags;
        MessageType = messageType;
        Reserved = reserved;
        RequestId = requestId;
        PayloadLength = payloadLength;
        Checksum = checksum;
    }

    /// <summary>
    /// Writes the fixed header to the provided destination span.
    /// </summary>
    /// <param name="destination">The span to write the fixed header to.</param>
    public void WriteToSpan(Span<byte> destination)
    {
        destination[0] = Version;
        destination[1] = Flags;
        destination[2] = MessageType;
        destination[3] = Reserved;

        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), RequestId);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), PayloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(12, 4), Checksum);
    }

    /// <summary>
    /// Reads a fixed header from the provided source span.
    /// </summary>
    /// <param name="source">The span to read the fixed header from.</param>
    /// <returns>A <see cref="FixedHeader"/> instance populated with data from the span.</returns>
    public static FixedHeader ReadFromSpan(ReadOnlySpan<byte> source)
    {
        return new FixedHeader(
            source[0],
            source[1],
            source[2],
            source[3],
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(12, 4))
        );
    }
}
