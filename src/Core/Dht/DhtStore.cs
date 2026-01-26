using System.Collections.Concurrent;
using NSec.Cryptography;

namespace FalconNode.Core.Dht;

/// <summary>
/// In-memory store for mutable records in the DHT.
/// </summary>
public class DhtStore
{
    /// <summary>
    /// In-memory storage for mutable records, keyed by the hex string of the owner's public key.
    /// </summary>
    private readonly ConcurrentDictionary<string, MutableRecord> _records = new();

    /// <summary>
    /// Logger instance for logging DHT store operations.
    /// </summary>
    private readonly ILogger<DhtStore> _logger;

    public DhtStore(ILogger<DhtStore> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to put a mutable record into the DHT store.
    /// </summary>
    /// <param name="newRecord">The mutable record to be added or updated.</param>
    /// <returns><c>True</c> if the record was added or updated; otherwise, <c>false</c>.</returns>
    public bool TryPut(MutableRecord newRecord)
    {
        if (newRecord.IsValid() == false)
        {
            _logger.LogWarning("Attempted to put invalid mutable record into DHT store.");
            return false;
        }

        string key = Convert.ToHexString(newRecord.Owner.Export(KeyBlobFormat.RawPublicKey));

        // Atomic update to ensure only the highest sequence number is stored
        _records.AddOrUpdate(
            key,
            // If the key does not exist, add the new record
            newRecord,
            // If the key exists, compare sequence numbers and update if the new one is higher
            (k, existingRecord) =>
            {
                if (newRecord.Sequence > existingRecord.Sequence)
                {
                    _logger.LogInformation(
                        $"Updated mutable record in DHT store for key {key} to sequence {newRecord.Sequence}."
                    );
                    return newRecord;
                }
                return existingRecord;
            }
        );

        return true;
    }

    /// <summary>
    /// Retrieves a mutable record by the owner's public key bytes.
    /// </summary>
    /// <param name="publicKeyBytes">The public key bytes of the record owner.</param>
    /// <returns>The mutable record if found; otherwise, <c>null</c>.</returns>
    public MutableRecord? Get(byte[] publicKeyBytes)
    {
        string key = Convert.ToHexString(publicKeyBytes);
        _records.TryGetValue(key, out var record);
        return record;
    }
}
