using FalconNode.Core.Dht;

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
    /// Initializes a new instance of the <see cref="NodeSettings"/> class with the specified node ID.
    /// </summary>
    /// <param name="nodeId">The unique identifier for the local node.</param>
    public NodeSettings(NodeId nodeId)
    {
        LocalNodeId = nodeId;
    }
}
