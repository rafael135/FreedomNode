using System.Buffers;
using System.Net;
using System.Numerics;
using System.Threading.Channels;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
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
    /// The routing table that maintains known contacts in the DHT.
    /// </summary>
    private readonly RoutingTable _routingTable;

    /// <summary>
    /// The node settings containing configuration details such as the local node ID.
    /// </summary>
    private readonly NodeSettings _settings;

    /// <summary>
    /// The request manager for handling network requests and responses.
    /// </summary>
    private readonly RequestManager _requestManager;

    /// <summary>
    /// The node logic worker responsible for sending requests and processing responses.
    /// </summary>
    private readonly ChannelWriter<OutgoingMessage> _outWriter;

    /// <summary>
    /// The logger instance for logging DHT-related information.
    /// </summary>
    private readonly ILogger<DhtService> _logger;

    public DhtService(
        RoutingTable routingTable,
        NodeSettings settings,
        RequestManager requestManager,
        ChannelWriter<OutgoingMessage> outWriter,
        ILogger<DhtService> logger
    )
    {
        _routingTable = routingTable;
        _settings = settings;
        _requestManager = requestManager;
        _outWriter = outWriter;
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
        uint requestId = _requestManager.NextId();

        // 1. Create payload with FIND_NODE request(0x03)
        int payloadSize = 32;
        int totalSize = FixedHeader.Size + payloadSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);

        try
        {
            FixedHeader
                .Create(0x03, requestId, targetId.Span)
                .WriteToSpan(buffer.AsSpan(0, FixedHeader.Size));

            // Payload (Target ID)
            targetId.Span.CopyTo(buffer.AsSpan(FixedHeader.Size));

            // 2. Register the request before sending
            Task<NetworkPacket> responseTask = _requestManager.RegisterRequestAsync(
                requestId,
                TimeSpan.FromSeconds(5)
            );

            // 3. Send the request
            OutgoingMessage msg = new OutgoingMessage(
                endpoint,
                buffer.AsMemory(0, totalSize),
                buffer
            );
            await _outWriter.WriteAsync(msg);

            // 4. Await the response
            NetworkPacket responsePacket = await responseTask;

            // 5. Validate and parse the response
            if (responsePacket.MessageType == 0x04)
            {
                try
                {
                    var response = FindNodeResponse.ReadFromSpan(responsePacket.Payload.Span);
                    return response.Contacts;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(responsePacket.OriginalBufferReference);
                }
            }
        }
        catch (Exception)
        {
            // Returns the send buffer if an error occurs before sending
            // (If sent successfully, ConnectionManager returns it)
            // ArrayPool<byte>.Shared.Return(buffer);
            throw;
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
