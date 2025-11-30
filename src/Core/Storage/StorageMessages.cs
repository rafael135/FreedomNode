namespace FalconNode.Core.Storage;

/// <summary>
/// Represents a request to store binary data in the storage system.
/// The payload consists of the data length (4 bytes) followed by the data itself.
/// The hash is not transmitted; the receiver computes it to verify integrity.
/// </summary>
public readonly struct StoreRequest
{
    // Format: [DataLength (4)] + [Data]
    // Note: We do not send the Hash. The receiver calculates it to ensure integrity.
    /// <summary>
    /// The binary data to be stored.
    /// </summary>
    public readonly ReadOnlyMemory<byte> Data;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreRequest"/> struct with the specified data.
    /// </summary>
    /// <param name="data">The binary data to be stored.</param>
    public StoreRequest(ReadOnlyMemory<byte> data) => Data = data;

    /// <summary>
    /// Creates a <see cref="StoreRequest"/> instance from the provided <paramref name="span"/>.
    /// Assumes the entire payload in <paramref name="span"/> is the data for the request.
    /// In production scenarios, additional metadata such as TTL and owner's signature may be included.
    /// </summary>
    /// <param name="span">A read-only span of bytes representing the payload data.</param>
    /// <returns>A <see cref="StoreRequest"/> initialized with the provided data.</returns>
    public static StoreRequest ReadFromSpan(ReadOnlySpan<byte> span)
    {
        // Simplification: Assume the entire payload is the data
        // In production, we would have metadata (TTL, Owner's Signature, etc)
        return new StoreRequest(span.ToArray());
    }
}

/// <summary>
/// Represents a request to fetch a blob identified by its SHA-256 hash.
/// </summary>
public readonly struct FetchRequest
{
    /// <summary>
    /// The SHA-256 hash identifier of the blob to be fetched.
    /// </summary>
    public readonly byte[] HashId; // 32 bytes

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchRequest"/> struct with the specified hash.
    /// </summary>
    /// <param name="hash">The SHA-256 hash identifier of the blob to be fetched.</param>
    public FetchRequest(byte[] hash) => HashId = hash;

    /// <summary>
    /// Reads a <see cref="FetchRequest"/> from the specified <paramref name="span"/>.
    /// </summary>
    /// <param name="span">The read-only byte span containing the fetch request data.</param>
    /// <returns>A <see cref="FetchRequest"/> instance constructed from the first 32 bytes of the span.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the length of <paramref name="span"/> is less than 32 bytes.
    /// </exception>
    public static FetchRequest ReadFromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length < 32)
        {
            throw new ArgumentException("Invalid Fetch Request");
        }

        return new FetchRequest(span.Slice(0, 32).ToArray());
    }
}
