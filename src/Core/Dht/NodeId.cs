using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FalconNode.Core.Dht;

/// <summary>
/// Represents a unique identifier for nodes in the Distributed Hash Table (DHT) network.
/// NodeId is a fixed-size (32 bytes) value, typically derived from cryptographic operations or random generation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NodeId : IEquatable<NodeId>, IComparable<NodeId>
{
    /// <summary>
    /// The fixed size (in bytes) of a NodeId. All NodeIds must be exactly 32 bytes.
    /// </summary>
    public const int Size = 32;

    /// <summary>
    /// The underlying byte array representing the NodeId value.
    /// </summary>
    private readonly byte[] _bytes;

    /// <summary>
    /// Gets a read-only span over the NodeId's bytes.
    /// Useful for efficient, non-allocating access to the identifier data.
    /// </summary>
    public ReadOnlySpan<byte> Span => _bytes;

    /// <summary>
    /// Initializes a new NodeId from a byte array.
    /// </summary>
    /// <param name="bytes">A byte array of length 32 representing the NodeId.</param>
    /// <exception cref="ArgumentException">Thrown if the array is not exactly 32 bytes.</exception>
    /// <remarks>
    /// This constructor does not copy the array; the reference is stored directly.
    /// </remarks>
    public NodeId(byte[] bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"NodeId must be {Size} bytes long.", nameof(bytes));
        _bytes = bytes;
    }

    /// <summary>
    /// Initializes a new NodeId from a read-only span of bytes.
    /// </summary>
    /// <param name="span">A span of length 32 representing the NodeId.</param>
    /// <exception cref="ArgumentException">Thrown if the span is not exactly 32 bytes.</exception>
    /// <remarks>
    /// The span is copied into a new array to ensure immutability.
    /// </remarks>
    public NodeId(ReadOnlySpan<byte> span)
    {
        if (span.Length != Size)
            throw new ArgumentException($"NodeId must be {Size} bytes long.", nameof(span));
        _bytes = span.ToArray();
    }

    /// <summary>
    /// Generates a new random NodeId using a cryptographically secure random number generator.
    /// </summary>
    /// <returns>A new <see cref="NodeId"/> instance with random bytes.</returns>
    public static NodeId Random()
    {
        var bytes = new byte[Size];
        RandomNumberGenerator.Fill(bytes);
        return new NodeId(bytes);
    }

    /// <summary>
    /// Calculates the XOR distance between two NodeIds.
    /// This is commonly used in DHTs to measure closeness between nodes.
    /// </summary>
    /// <param name="a">The first NodeId.</param>
    /// <param name="b">The second NodeId.</param>
    /// <returns>A byte array representing the XOR distance.</returns>
    public static byte[] Distance(NodeId a, NodeId b)
    {
        var result = new byte[Size];
        for (int i = 0; i < Size; i++)
        {
            result[i] = (byte)(a._bytes[i] ^ b._bytes[i]);
        }
        return result;
    }

    /// <summary>
    /// Compares this NodeId to another NodeId for ordering.
    /// </summary>
    /// <param name="other">The other NodeId to compare to.</param>
    /// <returns>
    /// A signed integer indicating the relative order:
    /// &lt; 0 if this instance precedes <paramref name="other"/>,
    /// 0 if they are equal,
    /// &gt; 0 if this instance follows <paramref name="other"/>.
    /// </returns>
    /// <remarks>
    /// This comparison is used for sorting NodeIds, e.g., in "FindClosest" algorithms in DHT implementations.
    /// </remarks>
    public int CompareTo(NodeId other)
    {
        return _bytes.AsSpan().SequenceCompareTo(other._bytes.AsSpan());
    }

    /// <summary>
    /// Checks equality between this NodeId and another NodeId.
    /// </summary>
    /// <param name="other">The other NodeId to compare with.</param>
    /// <returns>True if the NodeIds are equal; otherwise, false.</returns>
    public bool Equals(NodeId other) => _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <summary>
    /// Checks equality between this NodeId and another object.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns>True if <paramref name="obj"/> is a NodeId and is equal; otherwise, false.</returns>
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    /// <summary>
    /// Returns a hash code for this NodeId.
    /// </summary>
    /// <returns>An integer hash code suitable for use in hash tables.</returns>
    /// <remarks>
    /// The hash code is derived from the base64 string of the NodeId bytes.
    /// </remarks>
    public override int GetHashCode() => Convert.ToBase64String(_bytes).GetHashCode();

    /// <summary>
    /// Returns a short hexadecimal string representation of the NodeId (first 8 hex digits).
    /// </summary>
    /// <returns>A string containing the first 8 hex digits of the NodeId.</returns>
    public override string ToString() => Convert.ToHexString(_bytes)[..8];
}
