using System.Text.Json.Serialization;

namespace FalconNode.Core.FS;

/// <summary>
/// Represents a file manifest that contains metadata about a file.
/// The manifest includes the file name, content type, total size, and a list of chunk
/// </summary>
public record FileManifest
{
    /// <summary>
    /// Represents a file manifest that contains metadata about a file.
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