using System.Buffers;
using System.Net;
using System.Threading.Channels;
using FalconNode.Core.Dht;
using FalconNode.Core.FS;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
using FalconNode.Core.State;
using FalconNode.Core.Storage;
using FalconNode.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;

namespace FreedomNode.Tests;

public class NodeLogicWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_Handshake_RegistersPeer()
    {
        var inChannel = Channel.CreateBounded<NetworkPacket>(
            new BoundedChannelOptions(10)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
        var outChannel = Channel.CreateBounded<OutgoingMessage>(
            new BoundedChannelOptions(10)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );

        var peerTable = new PeerTable();
        var nodeSettings = new NodeSettings(NodeId.Random(), 40000);
        var blobStore = new BlobStore(new NullLogger<BlobStore>());
        var fileIngestor = new FileIngestor(blobStore);
        var routingTable = new RoutingTable(nodeSettings);
        var requestManager = new RequestManager();
        var logger = new NullLogger<NodeLogicWorker>();

        var worker = new NodeLogicWorker(
            inChannel,
            outChannel,
            requestManager,
            peerTable,
            routingTable,
            blobStore,
            fileIngestor,
            logger
        );

        using var cts = new CancellationTokenSource();

        // Start the worker loop via BackgroundService public StartAsync
        await worker.StartAsync(cts.Token);

        // Build a handshake payload from a fake peer
        var peerIdentity = Key.Create(SignatureAlgorithm.Ed25519);
        var peerOnion = Key.Create(KeyAgreementAlgorithm.X25519);

        var identityBytes = peerIdentity.Export(KeyBlobFormat.RawPublicKey);
        var onionBytes = peerOnion.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> signable = stackalloc byte[72];
        HandshakePayload.WriteSignableBytes(signable, identityBytes, onionBytes, now);

        byte[] signature = SignatureAlgorithm.Ed25519.Sign(peerIdentity, signable);

        var payload = new HandshakePayload(identityBytes, onionBytes, now, signature);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HandshakePayload.Size);
        try
        {
            payload.WriteToSpan(buffer.AsSpan(0, HandshakePayload.Size));

            var origin = new IPEndPoint(IPAddress.Loopback, 40321);
            var pkt = new NetworkPacket(
                origin,
                0x01,
                0,
                buffer.AsMemory(0, HandshakePayload.Size),
                buffer
            );

            await inChannel.Writer.WriteAsync(pkt);

            // Give some time for worker to process
            await Task.Delay(300);

            // The peer should be registered in the peer table
            Assert.True(peerTable.TryGetPeerKey(origin, out var registeredOnion));
            Assert.Equal(onionBytes, registeredOnion);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            cts.Cancel();
            try
            {
                await worker.StopAsync(CancellationToken.None);
            }
            catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_DhtLookup_SendsResponse()
    {
        var inChannel = Channel.CreateBounded<NetworkPacket>(
            new BoundedChannelOptions(10)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
        var outChannel = Channel.CreateBounded<OutgoingMessage>(
            new BoundedChannelOptions(10)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );

        var peerTable = new PeerTable();
        var nodeSettings = new NodeSettings(NodeId.Random(), 40001);
        var blobStore = new BlobStore(new NullLogger<BlobStore>());
        var fileIngestor = new FileIngestor(blobStore);
        var routingTable = new RoutingTable(nodeSettings);
        var requestManager = new RequestManager();
        var logger = new NullLogger<NodeLogicWorker>();

        // Pre-populate routing table with a contact
        var contactId = NodeId.Random();
        var contactEp = new IPEndPoint(IPAddress.Loopback, 12345);
        routingTable.AddContact(new Contact(contactId, contactEp));

        var worker = new NodeLogicWorker(
            inChannel,
            outChannel,
            requestManager,
            peerTable,
            routingTable,
            blobStore,
            fileIngestor,
            logger
        );

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Build FindNodeRequest payload
        var targetId = contactId; // request for the node we already have
        byte[] reqPayload = new byte[NodeId.Size];
        targetId.Span.CopyTo(reqPayload.AsSpan());

        byte[] buffer = ArrayPool<byte>.Shared.Rent(FixedHeader.Size + reqPayload.Length);
        try
        {
            // Write header (0x03 = FIND_NODE request)
            new FixedHeader(0x03, 0, (uint)reqPayload.Length).WriteToSpan(
                buffer.AsSpan(0, FixedHeader.Size)
            );
            reqPayload.CopyTo(buffer.AsSpan(FixedHeader.Size));

            var pkt = new NetworkPacket(
                new IPEndPoint(IPAddress.Loopback, 40000),
                0x03,
                0,
                buffer.AsMemory(FixedHeader.Size, reqPayload.Length),
                buffer
            );

            await inChannel.Writer.WriteAsync(pkt);

            // Allow some processing
            await Task.Delay(300);

            // Try read outgoing
            bool found = outChannel.Reader.TryRead(out var outMsg);
            Assert.True(found, "Expected a response to be sent on outgoing channel");

            // The response header should be 0x04 (FindNode response)
            var headerSpan = outMsg.Payload.Span.Slice(0, FixedHeader.Size);
            var respHeader = FixedHeader.ReadFromSpan(headerSpan);
            Assert.Equal((byte)0x04, respHeader.MessageType);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            cts.Cancel();
            try
            {
                await worker.StopAsync(CancellationToken.None);
            }
            catch { }
        }
    }
}
