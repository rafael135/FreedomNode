using System.Runtime.InteropServices;
using FalconNode.Core.Dht;
using NSec.Cryptography;

namespace FalconNode.Core.State;

/// <summary>
/// Holds configuration settings for the local node.
/// </summary>
public class NodeSettings
{
    /// <summary>
    /// The local node's unique identifier.
    /// </summary>
    public NodeId LocalNodeId { get; }

    /// <summary>
    /// The port number for node communication.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// The port number for peering communication.
    /// </summary>
    public int PeeringPort { get; }

    public Key StorageKey { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NodeSettings"/> class with the specified node ID.
    /// </summary>
    /// <param name="nodeId">The unique identifier for the local node.</param>
    /// <param name="port">The port number for node communication.</param>
    /// <param name="storageKey">The storage key for the node.</param>
    /// <param name="peeringPort">The port number for peering communication (optional).</param>
    public NodeSettings(NodeId nodeId, int port, Key storageKey, int peeringPort = 0)
    {
        LocalNodeId = nodeId;
        Port = port;
        StorageKey = storageKey;
        PeeringPort = peeringPort;
    }
}
