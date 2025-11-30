using System.Buffers.Binary;
using System.Net;
using FalconNode.Core.Network;

namespace FalconNode.Core.Dht;

public readonly struct FindNodeRequest
{
    public readonly NodeId TargetId;

    public FindNodeRequest(NodeId target) => TargetId = target;

    public void WriteToSpan(Span<byte> destination)
    {
        TargetId.Span.CopyTo(destination);
    }

    public static FindNodeRequest ReadFromSpan(ReadOnlySpan<byte> src)
    {
        return new FindNodeRequest(new NodeId(src.Slice(0, 32)));
    }
}

public readonly struct FindNodeResponse
{
    public readonly List<Contact> Contacts;

    public FindNodeResponse(List<Contact> contacts)
    {
        Contacts = contacts;
    }

    public int CalculateSize()
    {
        // Count
        int size = 1;

        foreach (var c in Contacts)
        {
            size += 32; // NodeId
            size += 1; // IP Length
            size += 2; // Port
            size += c.Endpoint.Address.GetAddressBytes().Length;
        }

        return size;
    }

    public void WriteToSpan(Span<byte> destination)
    {
        destination[0] = (byte)Contacts.Count;
        int offset = 1;

        foreach (var c in Contacts)
        {
            // NodeId
            c.Id.Span.CopyTo(destination.Slice(offset, 32));
            offset += 32;

            // IP Address
            var ipBytes = c.Endpoint.Address.GetAddressBytes();
            destination[offset] = (byte)ipBytes.Length;
            offset++;
            ipBytes.CopyTo(destination.Slice(offset, ipBytes.Length));
            offset += ipBytes.Length;

            // Port
            BinaryPrimitives.WriteUInt16BigEndian(
                destination.Slice(offset, 2),
                (ushort)c.Endpoint.Port
            );
            offset += 2;
        }
    }
}
