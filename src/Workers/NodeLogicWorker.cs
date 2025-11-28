using System.Buffers;
using System.Threading.Channels;
using FalconNode.Core.Messages;

namespace FalconNode.Workers;


/// <summary>
/// Worker responsible for processing incoming network packets and implementing node logic.
/// </summary>
public class NodeLogicWorker : BackgroundService
{
    private readonly ChannelReader<NetworkPacket> _incomingReader;
    private readonly ILogger<NodeLogicWorker> _logger;

    public NodeLogicWorker(Channel<NetworkPacket> channel, ILogger<NodeLogicWorker> logger)
    {
        _incomingReader = channel.Reader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Node Logic Worker started.");

        await foreach (var packet in _incomingReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                ProcessPacket(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing packet.");
            }
            finally
            {
                // Return the original buffer to the pool to avoid memory leaks
                ArrayPool<byte>.Shared.Return(packet.OriginalBufferReference);
            }
        }
    }


    /// <summary>
    /// Processes an incoming network packet based on its message type.
    /// </summary>
    /// <param name="packet">The network packet to process.</param>
    private void ProcessPacket(NetworkPacket packet)
    {
        _logger.LogInformation(
            $"Processing packet of type {packet.MessageType} received at {packet.Timestamp}"
        );

        var result = packet.MessageType switch
        {
            0x01 => this.HandleHandshake(packet),
            0x02 => this.HandleDhtLookup(packet),
            0x03 => this.HandleOnionRoute(packet),
            _ => this.HandleUnknown(packet),
        };
    }

    private bool HandleHandshake(NetworkPacket packet)
    {
        System.Console.WriteLine("Handling Handshake packet.");
        return true;
    }

    private bool HandleDhtLookup(NetworkPacket packet)
    {
        System.Console.WriteLine("Handling DHT Lookup packet.");
        return true;
    }

    private bool HandleOnionRoute(NetworkPacket packet)
    {
        System.Console.WriteLine("Handling Onion Route packet.");
        return true;
    }

    private bool HandleUnknown(NetworkPacket packet)
    {
        System.Console.WriteLine("Handling unknown packet.");
        return true;
    }
}
