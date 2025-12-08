using System.Text;
using System.Text.Json;
using FalconNode.Core.FS;
using FalconNode.Core.Storage;

namespace FalconNode.Core.Social;

/// <summary>
/// Manages user profile data, including storage, retrieval, and updates of profile information and posts.
/// Utilizes a blob store for persistence, a file ingestor for processing profile files, and maintains a local pointer to the current profile state.
/// Provides methods for publishing posts, updating profile information, and loading/saving profile states asynchronously.
/// </summary>
public class ProfileManager
{
    /// <summary>
    /// The blob store used for storing user profiles.
    /// </summary>
    private readonly BlobStore _blobStore;

    /// <summary>
    /// The file ingestor used for ingesting profile data.
    /// </summary>
    private readonly FileIngestor _fileIngestor;

    /// <summary>
    /// The local path to the head profile manifest.
    /// </summary>
    private readonly string _localHeadPath;

    /// <summary>
    /// The logger instance for logging profile management information.
    /// </summary>
    private readonly ILogger<ProfileManager> _logger;

    /// <summary>
    /// The current profile hash representing the latest user profile state.
    /// </summary>
    public string? CurrentProfileHash { get; private set; }

    /// <summary>
    /// Loads the current profile hash from the local head file if it exists.
    /// Updates <see cref="CurrentProfileHash"/> with the trimmed contents of the file.
    /// </summary>
    private void LoadLocalHead()
    {
        if (File.Exists(_localHeadPath))
        {
            CurrentProfileHash = File.ReadAllText(_localHeadPath).Trim();
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileManager"/> class.
    /// Manages user profile data, including storage and ingestion of profile files.
    /// </summary>
    /// <param name="blobStore">The blob storage service used for profile data persistence.</param>
    /// <param name="fileIngestor">The file ingestor responsible for processing profile files.</param>
    /// <param name="logger">The logger instance for logging profile management operations.</param>
    public ProfileManager(
        BlobStore blobStore,
        FileIngestor fileIngestor,
        ILogger<ProfileManager> logger
    )
    {
        _blobStore = blobStore;
        _fileIngestor = fileIngestor;
        _logger = logger;

        _localHeadPath = Path.Combine(AppContext.BaseDirectory, "data", "profile_head.txt");
        LoadLocalHead();
    }

    /// <summary>
    /// Publishes a new post by ingesting the content as an immutable file, updating the user's profile with the new post,
    /// and saving the updated profile state.
    /// </summary>
    /// <param name="content">The textual content of the post to be published.</param>
    /// <returns>
    /// A <see cref="Task{String}"/> representing the asynchronous operation, containing the identifier of the saved profile state.
    /// </returns>
    /// <remarks>
    /// The post content is stored as a file, and its hash is used as the post identifier. The user's profile is updated to include the new post,
    /// and the profile's last updated timestamp is refreshed.
    /// </remarks>
    public async Task<string> PublishPostAsync(string content)
    {
        // 1. Ingest post content as a file(imutable)
        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        // Note: "post.txt" is just a placeholder name; actual name is the hash of the content.
        string postHash = await _fileIngestor.IngestAsync(stream, "post.txt", "text/plain");

        _logger.LogInformation("Published new post with hash {PostHash}", postHash);

        // 2. Load current profile (or create new if none)
        UserProfile profile = await LoadProfileStateAsync() ?? new UserProfile();

        // 3. Update profile with new post
        List<string> newPosts = new List<string> { postHash };
        newPosts.AddRange(profile.PostIds);

        UserProfile newProfile = profile with
        {
            PostIds = newPosts,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        // 4. Save new profile state
        return await SaveProfileStateAsync(newProfile);
    }

    /// <summary>
    /// Asynchronously updates the user's profile information with the specified username and bio.
    /// Loads the current profile state, applies the updates, sets the updated timestamp,
    /// and saves the new profile state.
    /// </summary>
    /// <param name="username">The new username to set for the profile.</param>
    /// <param name="bio">The new bio to set for the profile.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a string
    /// indicating the outcome of the profile update.
    /// </returns>
    public async Task<string> UpdateInfoAsync(string username, string bio)
    {
        UserProfile profile = await LoadProfileStateAsync() ?? new UserProfile();

        UserProfile newProfile = profile with
        {
            Username = username,
            Bio = bio,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        // Save new profile state
        return await SaveProfileStateAsync(newProfile);
    }

    /// <summary>
    /// Serializes the given <see cref="UserProfile"/> to JSON, stores it in the blob store,
    /// updates the local pointer to the new profile state, and returns the hash of the stored profile.
    /// </summary>
    /// <param name="profile">The user profile to save.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the hash string of the saved profile state.
    /// </returns>
    private async Task<string> SaveProfileStateAsync(UserProfile profile)
    {
        // Serialize profile to JSON
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(profile);

        // Store JSON in blob store
        byte[] hashBytes = await _blobStore.StoreAsync(jsonBytes);
        string newHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Updates the local pointer to the new profile state
        CurrentProfileHash = newHash;
        await File.WriteAllTextAsync(_localHeadPath, newHash);

        _logger.LogInformation($"Saved new profile state with hash {newHash}");

        return newHash;
    }

    /// <summary>
    /// Asynchronously loads the current user's profile state from the blob store using the profile hash.
    /// Returns <c>null</c> if the profile hash is not set, the data cannot be retrieved, or deserialization fails.
    /// </summary>
    /// <returns>
    /// A <see cref="UserProfile"/> instance if successful; otherwise, <c>null</c>.
    /// </returns>
    private async Task<UserProfile?> LoadProfileStateAsync()
    {
        if (string.IsNullOrEmpty(CurrentProfileHash))
        {
            return null;
        }

        byte[]? data = await _blobStore.RetrieveBytesAsync(
            Convert.FromHexString(CurrentProfileHash)
        );

        if (data == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UserProfile>(data);
        }
        catch
        {
            return null;
        }
    }
}
