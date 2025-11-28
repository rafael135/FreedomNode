using System.Collections.Concurrent;
using System.Net;

namespace FalconNode.Core.State;

/// <summary>
/// Metadata associated with a peer in the peer table.
/// </summary>
public class PeerMetadata
{
    /// <summary>
    /// The last time the peer was seen.
    /// </summary>
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Reputation score of the peer.
    /// 0 - 100
    /// Higher is better.
    /// </summary>
    public int Reputation { get; set; }
}

/// <summary>
/// Manages a table of known peers in the network.
/// </summary>
public class PeerTable
{
    /// <summary>
    /// A thread-safe dictionary storing known peers and their associated metadata.
    /// </summary>
    private readonly ConcurrentDictionary<IPEndPoint, PeerMetadata> _knownPeers = new();

    /// <summary>
    /// Adds a new peer or updates the last seen time if it already exists.
    /// </summary>
    /// <param name="endpoint">The network endpoint of the peer.</param>
    public void AddOrUpdate(IPEndPoint endpoint)
    {
        _knownPeers.TryAdd(endpoint, new PeerMetadata { LastSeen = DateTime.UtcNow });
    }

    /// <summary>
    /// Retrieves a list of random peers from the peer table.
    /// </summary>
    /// <param name="count">The number of random peers to retrieve.</param>
    /// <returns>A list of random peer endpoints.</returns>
    public List<IPEndPoint> GetRandomPeers(int count)
    {
        return _knownPeers.Keys.Take(count).ToList();
    }
}
