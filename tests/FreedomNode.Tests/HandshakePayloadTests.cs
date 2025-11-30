using System.Buffers.Binary;
using FalconNode.Core.Messages;
using Xunit;

namespace FreedomNode.Tests;

public class HandshakePayloadTests
{
    [Fact]
    public void WriteAndRead_HandshakePayload_RoundTrip()
    {
        byte[] identity = new byte[32];
        byte[] onion = new byte[32];
        for (int i = 0; i < 32; i++) { identity[i] = (byte)i; onion[i] = (byte)(i + 1); }
        ulong timestamp = 1234567890123UL;
        byte[] signature = new byte[64];
        for (int i = 0; i < 64; i++) signature[i] = (byte)(255 - i);

        var payload = new HandshakePayload(identity, onion, timestamp, signature);

        byte[] buffer = new byte[HandshakePayload.Size];
        payload.WriteToSpan(buffer.AsSpan());

        var parsed = HandshakePayload.ReadFromSpan(buffer);

        Assert.Equal(identity, parsed.IdentityKey);
        Assert.Equal(onion, parsed.OnionKey);
        Assert.Equal(timestamp, parsed.Timestamp);
        Assert.Equal(signature, parsed.Signature);
    }

    [Fact]
    public void WriteSignableBytes_CorrectLengthAndContent()
    {
        byte[] identity = new byte[32];
        byte[] onion = new byte[32];
        for (int i = 0; i < 32; i++) { identity[i] = (byte)(i * 2); onion[i] = (byte)(i * 3); }
        ulong timestamp = 999_999_999_999UL;

        Span<byte> signable = stackalloc byte[72];
        HandshakePayload.WriteSignableBytes(signable, identity, onion, timestamp);

        // Check that the first 32 bytes are identity, next 32 are onion and last 8 are timestamp big-endian
        for (int i = 0; i < 32; i++) Assert.Equal(identity[i], signable[i]);
        for (int i = 0; i < 32; i++) Assert.Equal(onion[i], signable[32 + i]);

        ulong readTs = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(signable.Slice(64, 8));
        Assert.Equal(timestamp, readTs);
    }
}
