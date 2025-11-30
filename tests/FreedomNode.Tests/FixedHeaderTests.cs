using System.Buffers;
using FalconNode.Core.Network;
using Xunit;

namespace FreedomNode.Tests;

public class FixedHeaderTests
{
    [Fact]
    public void WriteAndRead_Header_RoundTrip()
    {
        var header = new FixedHeader(type: 0x01, reqId: 0x12345678, payloadLength: 1024);

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
