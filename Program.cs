using System.Threading.Channels;
using FalconNode;
using FalconNode.Core.Messages;
using FalconNode.Core.State;
using FalconNode.Workers;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

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

// Workers
builder.Services.AddHostedService<QuicListenerWorker>();
builder.Services.AddHostedService<NodeLogicWorker>();
builder.Services.AddHostedService<ConnectionManagerWorker>();

IHost host = builder.Build();
host.Run();
