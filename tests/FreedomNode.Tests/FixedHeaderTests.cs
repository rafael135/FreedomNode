using System.Buffers;
using System.IO.Hashing;
using FalconNode.Core.Network;
using Xunit;

namespace FreedomNode.Tests;

public class FixedHeaderTests
{
    [Fact]
    public void Create_Header_with_CRC32()
    {
        byte messageType = 0x01;
        uint requestId = 0x12345678;
        byte[] payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };

        var header = FixedHeader.Create(messageType, requestId, payload);

        Assert.Equal(1, header.Version);
        Assert.Equal(0, header.Flags);
        Assert.Equal(messageType, header.MessageType);
        Assert.Equal(0, header.Reserved);
        Assert.Equal(requestId, header.RequestId);
        Assert.Equal((uint)payload.Length, header.PayloadLength);

        // Compute expected CRC32
        uint expectedCrc = Crc32.HashToUInt32(payload);
        Assert.Equal(expectedCrc, header.Checksum);
    }

    [Fact]
    public void WriteAndRead_Header_RoundTrip()
    {
        var header = FixedHeader.Create(
            type: 0x01,
            requestId: 0x12345678,
            payload: Array.Empty<byte>()
        );

        byte[] buffer = ArrayPool<byte>.Shared.Rent(FixedHeader.Size);
        try
        {
            var span = buffer.AsSpan(0, FixedHeader.Size);
            header.WriteToSpan(span);

            var read = FixedHeader.ReadFromSpan(span);

            Assert.Equal(header.Version, read.Version);
            Assert.Equal(header.Flags, read.Flags);
            Assert.Equal(header.MessageType, read.MessageType);
            Assert.Equal(header.Reserved, read.Reserved);
            Assert.Equal(header.RequestId, read.RequestId);
            Assert.Equal(header.PayloadLength, read.PayloadLength);
            Assert.Equal(header.Checksum, read.Checksum);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
