using System.Buffers;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Channels;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
using FalconNode.Core.State;
using FalconNode.Workers;
using Microsoft.VisualBasic;
using NSec.Cryptography;

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
            new FixedHeader(0x03, requestId, (uint)payloadSize).WriteToSpan(
                buffer.AsSpan(0, FixedHeader.Size)
            );

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

    /// <summary>
    /// Publishes a mutable record to the DHT by sending PUT requests to the closest nodes to the record's owner's public key.
    /// </summary>
    /// <param name="record">The mutable record to be published.</param>
    /// <returns>A task representing the asynchronous publish operation.</returns>
    public async Task PublishRecordAsync(MutableRecord record)
    {
        // 1. Prepare the target ID based on the owner's public key
        // Prioritizes publishing to nodes closest to the owner's public key
        byte[] publicKeyBytes = record.Owner.Export(KeyBlobFormat.RawPublicKey);

        var targetId = new NodeId(SHA256.HashData(publicKeyBytes));

        // 2. Lookup closest nodes to the target ID
        var nodes = await LookupAsync(targetId);

        // 3. Send PUT requests to the closest nodes (Redundancy factor of 5)
        var tasks = nodes.Take(5).Select(node => SendPutRequest(node.Endpoint, record));
        await Task.WhenAll(tasks);

        _logger.LogInformation(
            $"Published mutable record with sequence {record.Sequence} to {nodes.Count} nodes."
        );
    }

    /// <summary>
    /// Retrieves a mutable record from the DHT for the specified target public key bytes.
    /// </summary>
    /// <param name="targetPublicKeyBytes">The raw bytes of the target public key.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the retrieved mutable record, or null if not found.</returns>
    public async Task<MutableRecord?> GetRecordAsync(byte[] targetPublicKeyBytes)
    {
        var targetId = new NodeId(SHA256.HashData(targetPublicKeyBytes));
        var nodes = await LookupAsync(targetId);

        MutableRecord? bestRecord = null;

        foreach (var node in nodes)
        {
            var record = await SendGetRequest(node.Endpoint, targetPublicKeyBytes);

            if (record != null && record.IsValid())
            {
                if (bestRecord == null || record.Sequence > bestRecord.Sequence)
                {
                    bestRecord = record;
                }
            }
        }

        return bestRecord;
    }

    /// <summary>
    /// Sends a PUT request to a specified endpoint to store a mutable record in the DHT.
    /// </summary>
    /// <param name="endpoint">The endpoint to which the PUT request will be sent.</param>
    /// <param name="record">The mutable record to be stored.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    private async Task SendPutRequest(IPEndPoint endpoint, MutableRecord record)
    {
        // OpCode 0x10 = PUT_VALUE
        var request = new PutValueRequest(record);
        int payloadSize = request.CalculateSize();
        int totalSize = FixedHeader.Size + payloadSize;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);

        try
        {
            // We use RequestId 0 because PUT is generally fire-and-forget (non-blocking)
            // or we can implement ACK later if desired.
            new FixedHeader(0x10, 0, (uint)payloadSize).WriteToSpan(
                buffer.AsSpan(0, FixedHeader.Size)
            );

            request.WriteToSpan(buffer.AsSpan(FixedHeader.Size));

            OutgoingMessage msg = new OutgoingMessage(
                endpoint,
                buffer.AsMemory(0, totalSize),
                buffer
            );
            await _outWriter.WriteAsync(msg);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            // Log error optional
        }
    }

    /// <summary>
    /// Sends a GET request to a specified endpoint to retrieve a mutable record from the DHT.
    /// </summary>
    /// <param name="endpoint">The endpoint to which the GET request will be sent.</param>
    /// <param name="targetPublicKeyBytes">The raw bytes of the target public key.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the retrieved mutable record, or null if not found.</returns>
    private async Task<MutableRecord?> SendGetRequest(
        IPEndPoint endpoint,
        byte[] targetPublicKeyBytes
    )
    {
        // OpCode 0x11 = GET_VALUE_REQ
        uint requestId = _requestManager.NextId();

        // Import the key to create the request
        var key = PublicKey.Import(
            SignatureAlgorithm.Ed25519,
            targetPublicKeyBytes,
            KeyBlobFormat.RawPublicKey
        );
        var request = new GetValueRequest(key);

        int payloadSize = 32; // Size of the PubKey Raw
        int totalSize = FixedHeader.Size + payloadSize;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);

        try
        {
            new FixedHeader(0x11, requestId, (uint)payloadSize).WriteToSpan(
                buffer.AsSpan(0, FixedHeader.Size)
            );
            request.WriteToSpan(buffer.AsSpan(FixedHeader.Size));

            var responseTask = _requestManager.RegisterRequestAsync(
                requestId,
                TimeSpan.FromSeconds(3)
            );

            OutgoingMessage msg = new OutgoingMessage(
                endpoint,
                buffer.AsMemory(0, totalSize),
                buffer
            );
            await _outWriter.WriteAsync(msg);

            var responsePacket = await responseTask;

            // OpCode 0x12 = GET_VALUE_RES
            if (responsePacket.MessageType == 0x12)
            {
                try
                {
                    var response = GetValueResponse.ReadFromSpan(responsePacket.Payload.Span);
                    if (response.Found && response.Record != null)
                    {
                        return response.Record;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(responsePacket.OriginalBufferReference);
                }
            }
        }
        catch
        {
            // Ignore timeouts/errors
        }

        return null;
    }
}
