using System.Buffers.Binary;
using System.Net;
using FalconNode.Core.Network;

namespace FalconNode.Core.Dht;

// Payload: [TargetID (32 bytes)]
/// <summary>
/// Represents a FIND_NODE request in the distributed hash table (DHT).
/// </summary>
public readonly struct FindNodeRequest
{
    /// <summary>
    /// The target node ID for which closest contacts are being requested.
    /// </summary>
    public readonly NodeId TargetId;

    public FindNodeRequest(NodeId target) => TargetId = target;

    /// <summary>
    /// Serializes the <see cref="FindNodeRequest"/> into the provided <see cref="Span{Byte}"/>.
    /// The serialization format consists of the 32-byte target node ID.
    /// </summary>
    /// <param name="destination">The span to which the serialized data will be written.</param>
    public void WriteToSpan(Span<byte> destination)
    {
        TargetId.Span.CopyTo(destination);
    }

    /// <summary>
    /// Deserializes a <see cref="FindNodeRequest"/> object from a binary span.
    /// The span is expected to contain a 32-byte target node ID.
    /// </summary>
    /// <param name="src">The span containing the serialized data.</param>
    /// <returns>A deserialized <see cref="FindNodeRequest"/> instance.</returns>
    public static FindNodeRequest ReadFromSpan(ReadOnlySpan<byte> src)
    {
        return new FindNodeRequest(new NodeId(src.Slice(0, 32)));
    }
}

// Payload: [Count (1 byte)] + N * [NodeID (32 bytes) + IP_Len (1 byte) + IP + Port (2 bytes)]
/// <summary>
/// Represents the response to a FIND_NODE request in the distributed hash table (DHT).
/// Contains a list of contacts that are closest to the requested target ID.
/// </summary>
public readonly struct FindNodeResponse
{
    /// <summary>
    /// The list of contacts returned in response to a FIND_NODE request.
    /// </summary>
    public readonly List<Contact> Contacts;

    public FindNodeResponse(List<Contact> contacts)
    {
        Contacts = contacts;
    }

    /// <summary>
    /// Calculates the size in bytes required to serialize the list of contacts.
    /// </summary>
    /// <returns>The total size in bytes needed for serialization.</returns>
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

    /// <summary>
    /// Serializes the list of contacts into the provided <see cref="Span{Byte}"/>.
    /// The serialization format is as follows:
    /// <list type="number">
    /// <item><description>First byte: Number of contacts.</description></item>
    /// <item><description>For each contact:
    ///   <list type="bullet">
    ///     <item><description>32 bytes: Node ID.</description></item>
    ///     <item><description>1 byte: Length of IP address.</description></item>
    ///     <item><description>Variable bytes: IP address.</description></item>
    ///     <item><description>2 bytes: Port (big-endian).</description></item>
    ///   </list>
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="destination">
    /// The <see cref="Span{Byte}"/> to write the serialized contacts to.
    /// Must be large enough to hold all serialized data.
    /// </param>
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

    /// <summary>
    /// Deserializes a <see cref="FindNodeResponse"/> object from a binary span.
    /// The span is expected to contain a list of contacts, each with a node ID, IP address, and port.
    /// Format:
    /// <list type="number">
    ///   <item><description>Contact count (1 byte)</description></item>
    ///   <item><description>For each contact:
    ///     <list type="bullet">
    ///       <item><description>Node ID (32 bytes)</description></item>
    ///       <item><description>IP address length (1 byte)</description></item>
    ///       <item><description>IP address (variable length)</description></item>
    ///       <item><description>Port (2 bytes, big-endian)</description></item>
    ///     </list>
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="src">The <see cref="ReadOnlySpan{Byte}"/> containing the serialized contacts.</param>
    /// <returns>A <see cref="FindNodeResponse"/> containing the deserialized contacts.</returns>
    public static FindNodeResponse ReadFromSpan(ReadOnlySpan<byte> src)
    {
        var contacts = new List<Contact>();
        int count = src[0];
        int offset = 1;

        for (int i = 0; i < count; i++)
        {
            // Reads ID
            byte[] idBytes = src.Slice(offset, 32).ToArray();
            NodeId id = new NodeId(idBytes);
            offset += 32;

            // Reads IP Address
            int ipLen = src[offset];
            offset++;
            byte[] ipBytes = src.Slice(offset, ipLen).ToArray();
            offset += ipLen;
            IPAddress ip = new IPAddress(ipBytes);

            // Reads Port
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(offset, 2));
            offset += 2;

            contacts.Add(new Contact(id, new IPEndPoint(ip, port)));
        }

        return new FindNodeResponse(contacts);
    }
}
