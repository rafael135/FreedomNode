using System.Text.Json.Serialization;

namespace FalconNode.Core.Social;

/// <summary>
/// Represents a user profile containing username, bio, avatar, posts, and update timestamp.
/// </summary>
public record UserProfile
{
    /// <summary>
    /// The username of the profile.
    /// </summary>
    public string Username { get; init; } = "Anonymous";

    /// <summary>
    /// The biography of the user.
    /// </summary>
    public string Bio { get; init; } = string.Empty;

    /// <summary>
    /// The identifier of the avatar image associated with the profile.
    /// </summary>
    public string? AvatarId { get; init; }

    /// <summary>
    /// The list of post identifiers associated with the profile.
    /// </summary>
    public List<string> PostIds { get; init; } = new();

    /// <summary>
    /// The timestamp of the last update to the profile.
    /// </summary>
    public long UpdatedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
