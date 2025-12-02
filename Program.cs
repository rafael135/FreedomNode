using System.Net;
using System.Threading.Channels;
using FalconNode.Core.Dht;
using FalconNode.Core.FS;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
using FalconNode.Core.State;
using FalconNode.Core.Storage;
using FalconNode.UI;
using FalconNode.Workers;

// --- ARGUMENT PARSING ---
// Example of use: dotnet run --port 5001 --seed 5000

int myPort = 5000; // Default port
int seedPort = 0; // 0 = No seeding
bool isDebugMode = false; // Default = no UI

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length)
    {
        myPort = int.Parse(args[i + 1]);
    }
    if (args[i] == "--seed" && i + 1 < args.Length)
    {
        seedPort = int.Parse(args[i + 1]);
    }
    if (args[i] == "--debug")
    {
        isDebugMode = true;
    }
}

Console.Title = $"FreedomNode - Port {myPort}";

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// --- LOGGING SETTINGS ---
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// --- SERVICES CONFIGURATION ---

// Local Node ID & Settings
NodeId localNodeId = NodeId.Random();
NodeSettings nodeSettings = new NodeSettings(localNodeId, myPort, seedPort);
builder.Services.AddSingleton(nodeSettings);

// Channels Configuration
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

builder.Services.AddSingleton(inChannel);
builder.Services.AddSingleton(outChannel);
builder.Services.AddSingleton<RequestManager>();

// Shared State
builder.Services.AddSingleton<PeerTable>();
builder.Services.AddSingleton<RoutingTable>();

// File Storage
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton<FileIngestor>();
builder.Services.AddSingleton<FileRetriever>();

// Workers
builder.Services.AddHostedService<QuicListenerWorker>();
builder.Services.AddHostedService<ConnectionManagerWorker>();

// NodeLogicWorker Registration Trick:
// We register it as Singleton first to inject it into TerminalUi later,
// then register it as HostedService referencing the same instance.
builder.Services.AddSingleton<NodeLogicWorker>();
builder.Services.AddHostedService<BackgroundService>(sp =>
    sp.GetRequiredService<NodeLogicWorker>()
);

if (isDebugMode)
{
    // User Interface
    builder.Services.AddSingleton<TerminalUi>();
}

// --- APPLICATION BOOTSTRAP ---

IHost host = builder.Build();

if (isDebugMode)
{
    // Starts the services on background
    await host.StartAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"=== Node Started on Port {myPort} ===");
    Console.WriteLine($"ID: {localNodeId}");
    if (seedPort > 0)
    {
        Console.WriteLine($"Seeding from Port: {seedPort}");
    }
    Console.ResetColor();

    // --- BOOTSTRAP LOGIC ---

    // If seed port is provided, initiate handshake automatically
    if (seedPort > 0)
    {
        var logicWorker = host.Services.GetRequiredService<NodeLogicWorker>();
        var writer = host.Services.GetRequiredService<Channel<OutgoingMessage>>().Writer;

        var seedEndpoint = new IPEndPoint(IPAddress.Loopback, seedPort);
        var handshakeMsg = logicWorker.CreateHandshakeMessage(seedEndpoint);

        await writer.WriteAsync(handshakeMsg);
        Console.WriteLine($"[System] Sent bootstrap handshake to {seedEndpoint}.");
    }

    // --- RUN UI ---
    // This blocks the main thread until the user types 'exit'
    var ui = host.Services.GetRequiredService<TerminalUi>();
    await ui.RunAsync();

    // Graceful shutdown
    await host.StopAsync();
}
else
{
    // Run the host normally without UI
    host.Run();
}
