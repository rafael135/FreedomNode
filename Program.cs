using System.Threading.Channels;
using FalconNode;
using FalconNode.Core.Messages;

var builder = Host.CreateApplicationBuilder(args);

var channel = Channel.CreateBounded<NetworkPacket>(
    new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    }
);

builder.Services.AddSingleton(channel);
builder.Services.AddHostedService<QuicListenerWorker>();
builder.Services.AddHostedService<NodeLogicWorker>();

var host = builder.Build();
host.Run();
