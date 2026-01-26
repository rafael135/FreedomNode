using System.Buffers.Binary;
using NSec.Cryptography;

namespace FalconNode.Core.Dht;

/// <summary>
/// Represents a mutable record in the DHT, which includes an owner public key, sequence number, value, and signature.
/// </summary>
public class MutableRecord
{
    /// <summary>
    /// The public key of the owner of the mutable record.
    /// </summary>
    public PublicKey Owner { get; }

    /// <summary>
    /// The sequence number of the mutable record.
    /// </summary>
    public ulong Sequence { get; }

    /// <summary>
    /// The value of the mutable record.
    /// </summary>
    public byte[] Value { get; }

    /// <summary>
    /// The signature of the mutable record. Ed25519 of (Value + Sequence)
    /// </summary>
    public byte[] Signature { get; }

    public MutableRecord(PublicKey owner, ulong sequence, byte[] value, byte[] signature)
    {
        Owner = owner;
        Sequence = sequence;
        Value = value;
        Signature = signature;
    }

    /// <summary>
    /// Validates the mutable record's signature.
    /// </summary>
    /// <returns><c>true</c> if the signature is valid; otherwise, <c>false</c>.</returns>
    public bool IsValid()
    {
        // Recreate the payload that was signed: [Sequence (8 bytes) | Value (variable length)]
        int dataSize = 8 + Value.Length;
        Span<byte> signableData = stackalloc byte[dataSize];

        BinaryPrimitives.WriteUInt64BigEndian(signableData.Slice(0, 8), Sequence);
        Value.CopyTo(signableData.Slice(8));

        return SignatureAlgorithm.Ed25519.Verify(Owner, signableData, Signature);
    }

    /// <summary>
    /// Creates and signs a new MutableRecord using the provided private key, sequence number, and value string.
    /// </summary>
    /// <param name="privateKey">The private key used to sign the record.</param>
    /// <param name="sequence">The sequence number of the record.</param>
    /// <param name="valueString">The value of the record as a string.</param>
    /// <returns>A new signed MutableRecord.</returns>
    public static MutableRecord SignAndCreate(Key privateKey, ulong sequence, string valueString)
    {
        byte[] value = System.Text.Encoding.UTF8.GetBytes(valueString);

        // Payload: [Sequence (8 bytes) | Value (variable length)]
        int dataSize = 8 + value.Length;
        byte[] signableData = new byte[dataSize];

        BinaryPrimitives.WriteUInt64BigEndian(signableData.AsSpan(0, 8), sequence);
        value.CopyTo(signableData.AsSpan(8));

        // Sign the payload
        byte[] signature = SignatureAlgorithm.Ed25519.Sign(privateKey, signableData);

        return new MutableRecord(privateKey.PublicKey, sequence, value, signature);
    }

    public int CalculateSize()
    {
        // Owner (32 bytes) + Sequence (8 bytes) + Signature (64 bytes) + Value Length (2 bytes) + Value
        return 32 + 8 + 64 + 2 + Value.Length;
    }

    public void WriteToSpan(Span<byte> destination)
    {
        int offset = 0;

        // Write Owner Public Key (32 bytes)
        byte[] publicKeyBytes = Owner.Export(KeyBlobFormat.RawPublicKey);
        publicKeyBytes.CopyTo(destination.Slice(offset, 32));
        offset += 32;

        // Write Sequence (8 bytes)
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(offset, 8), Sequence);
        offset += 8;

        // Write Signature (64 bytes)
        Signature.CopyTo(destination.Slice(offset, 64));
        offset += 64;

        // Write Value Length (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset, 2), (ushort)Value.Length);
        offset += 2;

        // Write Value (variable length)
        Value.CopyTo(destination.Slice(offset));
    }

    /// <summary>
    /// Deserializes a MutableRecord from the provided <see cref="ReadOnlySpan{Byte}"/>.
    /// </summary>
    /// <param name="src">The source span containing the serialized MutableRecord.</param>
    /// <returns>Returns a deserialized MutableRecord instance.</returns>
    public static MutableRecord ReadFromSpan(ReadOnlySpan<byte> src)
    {
        int offset = 0;

        // Read Owner Public Key (32 bytes)
        PublicKey ownerKey = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            src.Slice(offset, 32),
            KeyBlobFormat.RawPublicKey
        );
        offset += 32;

        // Read Sequence (8 bytes)
        ulong sequence = BinaryPrimitives.ReadUInt64BigEndian(src.Slice(offset, 8));
        offset += 8;

        // Read Signature (64 bytes)
        byte[] signature = src.Slice(offset, 64).ToArray();
        offset += 64;

        // Read Value Length (2 bytes)
        ushort valueLength = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(offset, 2));
        offset += 2;

        // Read Value (variable length)
        byte[] value = src.Slice(offset, valueLength).ToArray();

        return new MutableRecord(ownerKey, sequence, value, signature);
    }
}
