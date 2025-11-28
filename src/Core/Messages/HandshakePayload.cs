using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FalconNode.Core.Messages;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct HandshakePayload
{
    public const int Size = 136; // 32 + 32 + 8 + 64

    public readonly byte[] IdentityKey; // 32 bytes
    public readonly byte[] OnionKey; // 32 bytes
    public readonly ulong Timestamp; // 8 bytes

    public readonly byte[] Signature; // 64 bytes

    public HandshakePayload(byte[] identityKey, byte[] onionKey, ulong timestamp, byte[] signature)
    {
        IdentityKey = identityKey;
        OnionKey = onionKey;
        Timestamp = timestamp;
        Signature = signature;
    }

    /// <summary>
    /// Writes the handshake payload to the provided destination span.
    /// Zero allocation method for high-performance scenarios.
    /// </summary>
    /// <param name="destination">The span to write the handshake payload to.</param>
    public void WriteToSpan(Span<byte> destination)
    {
        IdentityKey.CopyTo(destination.Slice(0, 32));
        OnionKey.CopyTo(destination.Slice(32, 32));
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(64, 8), Timestamp);
        Signature.CopyTo(destination.Slice(72, 64));
    }

    /// <summary>
    /// Reads a handshake payload from the provided source span.
    /// </summary>
    /// <param name="source">The span to read the handshake payload from.</param>
    /// <returns>Returns a HandshakePayload instance.</returns>
    public static HandshakePayload ReadFromSpan(ReadOnlySpan<byte> source)
    {
        byte[] identityKey = source.Slice(0, 32).ToArray();
        byte[] onionKey = source.Slice(32, 32).ToArray();
        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(source.Slice(64, 8));
        byte[] signature = source.Slice(72, 64).ToArray();

        return new HandshakePayload(identityKey, onionKey, timestamp, signature);
    }

    /// <summary>
    /// Writes the signable bytes for the handshake payload to the provided destination span.
    /// </summary>
    /// <param name="destination">The span to write the signable bytes to.</param>
    /// <param name="idKey">The identity key bytes.</param>
    /// <param name="onionKey">The onion key bytes.</param>
    /// <param name="timestamp">The timestamp value.</param>
    public static void WriteSignableBytes(
        Span<byte> destination,
        ReadOnlySpan<byte> idKey,
        ReadOnlySpan<byte> onionKey,
        ulong timestamp
    )
    {
        idKey.CopyTo(destination.Slice(0, 32));
        onionKey.CopyTo(destination.Slice(32, 32));
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(64, 8), timestamp);
    }
}
