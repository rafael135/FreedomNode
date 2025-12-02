using System.Net;
using System.Numerics;
using FalconNode.Core.Messages;
using FalconNode.Core.State;
using FalconNode.Workers;

namespace FalconNode.Core.Dht;

/// <summary>
/// Provides distributed hash table (DHT) operations for peer-to-peer node discovery and routing.
/// Manages lookup queries, routing table updates, and bootstrap procedures for the local node.
/// </summary>
public class DhtService
{
    /// <summary>
    /// The NodeLogicWorker instance used for sending requests and receiving responses from peer nodes.
    /// </summary>
    private readonly NodeLogicWorker _nodeLogicWorker;

    /// <summary>
    /// The routing table that maintains known contacts in the DHT.
    /// </summary>
    private readonly RoutingTable _routingTable;

    /// <summary>
    /// The node settings containing configuration details such as the local node ID.
    /// </summary>
    private readonly NodeSettings _settings;

    /// <summary>
    /// The logger instance for logging DHT-related information.
    /// </summary>
    private readonly ILogger<DhtService> _logger;

    public DhtService(
        NodeLogicWorker nodeLogicWorker,
        RoutingTable routingTable,
        NodeSettings settings,
        ILogger<DhtService> logger
    )
    {
        _nodeLogicWorker = nodeLogicWorker;
        _routingTable = routingTable;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Performs a lookup operation in the distributed hash table (DHT) to find the closest contacts to the specified <paramref name="targetId"/>.
    /// The method iteratively queries peers for closer contacts until no improvement is found or the shortlist is exhausted.
    /// </summary>
    /// <param name="targetId">The <see cref="NodeId"/> to search for in the DHT.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation, with a result of a <see cref="List{Contact}"/>
    /// containing the closest contacts found to the <paramref name="targetId"/>.
    /// </returns>
    public async Task<List<Contact>> LookupAsync(NodeId targetId)
    {
        // 1. Initializes with the closest known nodes to the target ID.
        List<Contact> closestNodes = _routingTable.FindClosest(targetId, 20);
        HashSet<NodeId> visited = new HashSet<NodeId>();

        // Candidate nodes to query
        List<Contact> shortList = new List<Contact>(closestNodes);

        bool improved = true;

        while (improved && shortList.Count > 0)
        {
            improved = false;

            // Select up to 3 top unvisited candidates
            List<Contact> candidates = shortList
                .Where(c => !visited.Contains(c.Id))
                .OrderBy(c => NodeId.Distance(c.Id, targetId))
                .Take(3)
                .ToList();

            if (candidates.Count == 0)
            {
                break;
            }

            // Query each candidate in parallel
            var tasks = candidates.Select(async candidate =>
            {
                visited.Add(candidate.Id);
                try
                {
                    return await QueryPeerAsync(candidate.Endpoint, targetId);
                }
                catch
                {
                    // In case of failure or timeout, return an empty list
                    return new List<Contact>();
                }
            });

            var results = await Task.WhenAll(tasks);

            // Process results and update shortList
            foreach (var contacts in results)
            {
                foreach (var c in contacts)
                {
                    // If the contact is not already in the shortlist and is not the local node, add it
                    if (
                        !shortList.Any(x =>
                            x.Id.Equals(c.Id) && !c.Id.Equals(_settings.LocalNodeId)
                        )
                    )
                    {
                        shortList.Add(c);
                        improved = true;
                    }
                }
            }

            // Re-sort and trim the shortlist to the closest 20 contacts
            shortList = shortList
                .OrderBy(c => new BigInteger(NodeId.Distance(c.Id, targetId), true, true))
                .Take(20)
                .ToList();
        }

        return shortList;
    }

    /// <summary>
    /// Sends a query to a peer node to find contacts associated with the specified target node ID.
    /// </summary>
    /// <param name="endpoint">The <see cref="IPEndPoint"/> of the peer to query.</param>
    /// <param name="targetId">The <see cref="NodeId"/> representing the target node to search for.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation, containing a list of <see cref="Contact"/> objects
    /// returned by the peer. If the response is not of the expected type, an empty list is returned.
    /// </returns>
    private async Task<List<Contact>> QueryPeerAsync(IPEndPoint endpoint, NodeId targetId)
    {
        // Create payload with FIND_NODE request
        byte[] payload = new byte[32];
        targetId.Span.CopyTo(payload);

        // Send request and wait for response
        NetworkPacket responsePacket = await _nodeLogicWorker.SendRequestAndWaitAsync(
            endpoint,
            0x03,
            payload
        );

        // Parses the response (0x04 = FIND_NODE_RES)
        if (responsePacket.MessageType == 0x04)
        {
            // Deserializa lista de contatos (Lógica simplificada aqui)
            // var response = FindNodeResponse.ReadFromSpan(responsePacket.Payload.Span);
            // return response.Contacts;

            // NOTA: Você precisará expor o método de leitura do FindNodeResponse
            return new List<Contact>(); // Placeholder
        }

        return new List<Contact>();
    }

    /// <summary>
    /// Initializes the DHT service by performing a lookup for the local node ID.
    /// Logs a message upon successful completion of the bootstrap process.
    /// </summary>
    /// <returns>A task representing the asynchronous bootstrap operation.</returns>
    public async Task BootstrapAsync()
    {
        // Perform initial lookup for the local node ID to populate the routing table
        // and make the node known in the DHT network.
        await LookupAsync(_settings.LocalNodeId);
        _logger.LogInformation("DHT Bootstrap completed.");
    }
}
