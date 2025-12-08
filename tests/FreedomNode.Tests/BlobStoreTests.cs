using System;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using FalconNode.Core.Storage;
using FalconNode.Core.State;
using FalconNode.Core.Dht;
using NSec.Cryptography;
using Xunit;

namespace FreedomNode.Tests;

public class BlobStoreTests : IDisposable
{
    private readonly BlobStore _store;
    private readonly string _baseDir;

    public BlobStoreTests()
    {
        // BlobStore writes under AppContext.BaseDirectory/data/blobs/
        // Using test run directory is OK; tests must clean up after themselves.
        var storageKey = Key.Create(AeadAlgorithm.ChaCha20Poly1305);
        var nodeSettings = new NodeSettings(NodeId.Random(), 0, storageKey);
        _store = new BlobStore(nodeSettings, new NullLogger<BlobStore>());
        _baseDir = Path.Combine(AppContext.BaseDirectory, "data", "blobs");
        Directory.CreateDirectory(_baseDir);
    }

    [Fact]
    public async Task StoreAndRetrieve_Blob_RoundTrip()
    {
        byte[] data = Encoding.UTF8.GetBytes("hello blob store test");

        byte[] hash = await _store.StoreAsync(data);

        // Ensure file exists
        string filename = Path.Combine(_baseDir, Convert.ToHexString(hash).ToLowerInvariant());
        Assert.True(File.Exists(filename));

        byte[]? retrieved = await _store.RetrieveBytesAsync(hash);
        Assert.NotNull(retrieved);
        Assert.Equal(data, retrieved);
    }

    [Fact]
    public async Task Store_SkipsExistingBlob()
    {
        byte[] data = Encoding.UTF8.GetBytes("duplicate test");
        byte[] hash1 = await _store.StoreAsync(data);
        byte[] hash2 = await _store.StoreAsync(data);

        Assert.Equal(Convert.ToHexString(hash1), Convert.ToHexString(hash2));
    }

    public void Dispose()
    {
        // Cleanup created files
        try
        {
            if (Directory.Exists(_baseDir))
            {
                foreach (var file in Directory.EnumerateFiles(_baseDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }
}
