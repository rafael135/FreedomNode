using System.Text;
using System.Text.Json;
using FalconNode.Core.Dht;
using FalconNode.Core.FS;
using FalconNode.Core.State;
using FalconNode.Core.Storage;
using NSec.Cryptography;

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
    /// The local path to the head profile manifest hash.
    /// </summary>
    private readonly string _localHeadPath;

    /// <summary>
    /// The local path to the sequence number persistence file.
    /// </summary>
    private readonly string _localSeqPath;

    private readonly DhtService _dhtService;

    /// <summary>
    /// The Identity Key (Ed25519) used to SIGN mutable records.
    /// Distinct from the Storage Key.
    /// </summary>
    private readonly Key _myIdentityKey;

    /// <summary>
    /// The current sequence number for the mutable record versioning.
    /// </summary>
    private ulong _currentSequence = 0;

    /// <summary>
    /// The logger instance for logging profile management information.
    /// </summary>
    private readonly ILogger<ProfileManager> _logger;

    /// <summary>
    /// The current profile hash representing the latest user profile state.
    /// </summary>
    public string? CurrentProfileHash { get; private set; }

    /// <summary>
    /// Loads the current profile hash and sequence from local files if they exist.
    /// </summary>
    private void LoadLocalState()
    {
        if (File.Exists(_localHeadPath))
        {
            CurrentProfileHash = File.ReadAllText(_localHeadPath).Trim();
        }

        if (File.Exists(_localSeqPath))
        {
            string seqStr = File.ReadAllText(_localSeqPath).Trim();
            if (ulong.TryParse(seqStr, out ulong seq))
            {
                _currentSequence = seq;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileManager"/> class.
    /// Manages user profile data, including storage and ingestion of profile files.
    /// </summary>
    /// <param name="nodeSettings">The node settings containing configuration information.</param>
    /// <param name="dhtService">The DHT service used for distributed hash table operations.</param>
    /// <param name="blobStore">The blob storage service used for profile data persistence.</param>
    /// <param name="fileIngestor">The file ingestor responsible for processing profile files.</param>
    /// <param name="logger">The logger instance for logging profile management operations.</param>
    public ProfileManager(
        NodeSettings nodeSettings,
        DhtService dhtService,
        BlobStore blobStore,
        FileIngestor fileIngestor,
        ILogger<ProfileManager> logger
    )
    {
        _dhtService = dhtService;
        _blobStore = blobStore;
        _fileIngestor = fileIngestor;
        _logger = logger;

        string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        _localHeadPath = Path.Combine(dataDir, "profile_head.txt");
        _localSeqPath = Path.Combine(dataDir, "profile_seq.txt");

        // Identity Key Loading Logic
        // We do NOT use nodeSettings.StorageKey because that is symmetric (ChaCha20).
        // We need an asymmetric key (Ed25519) for signing.
        string identityKeyPath = Path.Combine(dataDir, "identity.key");

        if (File.Exists(identityKeyPath))
        {
            byte[] bytes = File.ReadAllBytes(identityKeyPath);
            _myIdentityKey = Key.Import(
                SignatureAlgorithm.Ed25519,
                bytes,
                KeyBlobFormat.RawPrivateKey
            );
        }
        else
        {
            _myIdentityKey = Key.Create(SignatureAlgorithm.Ed25519);
            File.WriteAllBytes(identityKeyPath, _myIdentityKey.Export(KeyBlobFormat.RawPrivateKey));
            _logger.LogInformation("Generated new Identity Key (Ed25519).");
        }

        LoadLocalState();
    }

    /// <summary>
    /// Publishes a new post by ingesting the content as an immutable file, updating the user's profile with the new post,
    /// and saving the updated profile state.
    /// </summary>
    /// <param name="content">The textual content of the post to be published.</param>
    /// <returns>
    /// A <see cref="Task{String}"/> representing the asynchronous operation, containing the identifier of the saved profile state.
    /// </returns>
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
    /// updates the local pointer to the new profile state, increments the sequence,
    /// and publishes the Mutable Record to the DHT.
    /// </summary>
    /// <param name="profile">The user profile to save.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the hash string of the saved profile state.
    /// </returns>
    private async Task<string> SaveProfileStateAsync(UserProfile profile)
    {
        // 1. Serialize profile to JSON
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(profile);

        // 2. Store JSON in blob store (Immutable Hash)
        byte[] hashBytes = await _blobStore.StoreAsync(jsonBytes);
        string newHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // 3. Update local state
        CurrentProfileHash = newHash;
        _currentSequence++; // Increment sequence for new version

        // Persist local state
        await File.WriteAllTextAsync(_localHeadPath, newHash);
        await File.WriteAllTextAsync(_localSeqPath, _currentSequence.ToString());

        _logger.LogInformation(
            $"Saved new profile state with hash {newHash} (Seq {_currentSequence})"
        );

        // 4. Publish Mutable Record to DHT (Fire-and-forget)
        try
        {
            var record = MutableRecord.SignAndCreate(
                _myIdentityKey,
                _currentSequence,
                newHash // The value of the record is the Hash of the Profile JSON
            );

            _ = _dhtService.PublishRecordAsync(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish mutable record to DHT.");
        }

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
            return JsonSerializer.Deserialize(data, FreedomNodeJsonContext.Default.UserProfile);
        }
        catch
        {
            return null;
        }
    }
}
