using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using FalconNode.Core.State;

namespace FalconNode.Core.Dht;

public class Contact
{
    /// <summary>
    /// The unique identifier of the contact.
    /// </summary>
    public NodeId Id { get; }

    /// <summary>
    /// The network endpoint of the contact.
    /// </summary>
    public IPEndPoint Endpoint { get; }

    /// <summary>
    /// The last time the contact was seen.
    /// </summary>
    public DateTime LastSeen { get; set; }

    public Contact(NodeId id, IPEndPoint endpoint)
    {
        Id = id;
        Endpoint = endpoint;
        LastSeen = DateTime.UtcNow;
    }
}

public class RoutingTable
{
    private readonly NodeId _localNodeId;
    private readonly List<List<Contact>> _buckets;
    private const int K = 20; // Bucket size (base redundancy)

    public RoutingTable(NodeSettings settings)
    {
        _localNodeId = settings.LocalNodeId;
        _buckets = new List<List<Contact>>(256);

        for (int i = 0; i < 256; i++)
        {
            _buckets.Add(new List<Contact>(K));
        }
    }

    private int GetBucketIndex(NodeId otherId)
    {
        var distance = NodeId.Distance(_localNodeId, otherId);

        int zeros = 0;
        foreach (var b in distance)
        {
            if (b == 0)
            {
                zeros += 8;
            }
            else
            {
                int temp = b;
                while ((temp & 0x80) == 0)
                {
                    zeros++;
                    temp <<= 1;
                }
            }
        }

        return Math.Min(255, zeros);
    }

    public void AddContact(Contact contact)
    {
        if (contact.Id.Equals(_localNodeId))
            return;

        int bucketIndex = GetBucketIndex(contact.Id);
        var bucket = _buckets[bucketIndex];

        lock (bucket)
        {
            var existing = bucket.FirstOrDefault(c => c.Id.Equals(contact.Id));

            if (existing != null)
            {
                bucket.Remove(existing);
                bucket.Add(existing); // Move to the end (most recently seen)
                existing.LastSeen = DateTime.UtcNow;
            }
            else
            {
                if (bucket.Count < K)
                {
                    bucket.Add(contact);
                }
                else
                {
                    // Bucket cheio! Política de Kademlia:
                    // Pingar o mais antigo. Se responder, move pro final e descarta o novo.
                    // Se não responder, deleta o antigo e insere o novo.
                    // (Para simplificar o protótipo: descartamos o novo por enquanto)
                }
            }
        }
    }

    public List<Contact> FindClosest(NodeId targetId, int count = K)
    {
        List<Contact> allContacts;

        lock (_buckets)
        {
            allContacts = _buckets.SelectMany(b => b).ToList();
        }

        return allContacts
            .OrderBy(c => new BigInteger(
                NodeId.Distance(c.Id, targetId),
                isUnsigned: true,
                isBigEndian: true
            ))
            .Take(count)
            .ToList();
    }
}
