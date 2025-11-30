using System.Threading.Channels;
using FalconNode;
using FalconNode.Core.Dht;
using FalconNode.Core.Messages;
using FalconNode.Core.State;
using FalconNode.Core.Storage;
using FalconNode.Workers;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Local Node ID
NodeId localNodeId = NodeId.Random();
NodeSettings nodeSettings = new NodeSettings(localNodeId);
builder.Services.AddSingleton(nodeSettings);

Channel<NetworkPacket> inChannel = Channel.CreateBounded<NetworkPacket>(
    new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    }
);

Channel<OutgoingMessage> outChannel = Channel.CreateBounded<OutgoingMessage>(
    new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    }
);

// Channels
builder.Services.AddSingleton(inChannel);
builder.Services.AddSingleton(outChannel);

// Shared State
builder.Services.AddSingleton<PeerTable>();
builder.Services.AddSingleton<RoutingTable>();

// File Storage
builder.Services.AddSingleton<BlobStore>();

// Workers
builder.Services.AddHostedService<QuicListenerWorker>();
builder.Services.AddHostedService<NodeLogicWorker>();
builder.Services.AddHostedService<ConnectionManagerWorker>();

IHost host = builder.Build();
host.Run();
