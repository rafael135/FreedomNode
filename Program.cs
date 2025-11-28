using System.Threading.Channels;
using FalconNode;
using FalconNode.Core.Messages;
using FalconNode.Core.State;
using FalconNode.Workers;

var builder = Host.CreateApplicationBuilder(args);

var inChannel = Channel.CreateBounded<NetworkPacket>(
    new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    }
);

var outChannel = Channel.CreateBounded<OutgoingMessage>(
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

var host = builder.Build();
host.Run();
