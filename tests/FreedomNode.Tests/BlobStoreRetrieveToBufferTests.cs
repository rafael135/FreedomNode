using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FalconNode.Core.Dht;
using FalconNode.Core.State;
using FalconNode.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Xunit;

namespace FreedomNode.Tests;

public class BlobStoreRetrieveToBufferTests : IDisposable
{
    private readonly BlobStore _store;
    private readonly string _baseDir;

    public BlobStoreRetrieveToBufferTests()
    {
        var storageKey = Key.Create(AeadAlgorithm.ChaCha20Poly1305);
        var nodeSettings = new NodeSettings(NodeId.Random(), 0, storageKey);

        _store = new BlobStore(nodeSettings, new NullLogger<BlobStore>());
        _baseDir = Path.Combine(AppContext.BaseDirectory, "data", "blobs");
        Directory.CreateDirectory(_baseDir);
    }

    [Fact]
    public async Task RetrieveToBufferAsync_WritesPlaintextIntoDestination()
    {
        byte[] data = Encoding.UTF8.GetBytes("buffer retrieval test");

        byte[] hash = await _store.StoreAsync(data);

        // Destination must have enough capacity for plaintext
        byte[] destination = new byte[data.Length];

        int bytesWritten = await _store.RetrieveToBufferAsync(hash, destination);

        Assert.Equal(data.Length, bytesWritten);
        Assert.Equal(data, destination);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
            {
                foreach (var file in Directory.EnumerateFiles(_baseDir))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
    }
}
