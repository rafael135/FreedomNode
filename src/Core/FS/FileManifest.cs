using System.Text.Json.Serialization;

namespace FalconNode.Core.FS;

/// <summary>
/// Represents a file manifest containing metadata and chunk references for content-addressed storage and retrieval.
/// </summary>
/// <remarks>
/// Manifests are serialized to JSON and stored in the blob store. Each manifest references one or more
/// content-addressed chunks (256 KiB by default) that together comprise the complete file. The manifest
/// includes the file name, MIME type, total size, and ordered list of chunk hashes for reassembly.
/// </remarks>
public record FileManifest
{
    /// <summary>
    /// The original file name.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// The content type of the file, defaulting to "application/octet-stream".
    /// </summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>
    /// The size of the file in bytes.
    /// </summary>
    public long TotalSize { get; init; }

    /// <summary>
    /// A list of chunk identifiers that make up the file.
    /// Each chunk is represented by a string identifier.
    /// </summary>
    public List<string> Chunks { get; init; } = new();
}