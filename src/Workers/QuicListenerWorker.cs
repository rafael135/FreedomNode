using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Threading.Channels;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
using FalconNode.Core.State;

namespace FalconNode.Workers;

/// <summary>
/// Worker responsible for listening for incoming QUIC connections and processing incoming network packets.
/// </summary>
public class QuicListenerWorker : BackgroundService
{
    /// <summary>
    /// Channel writer for incoming network packets.
    /// </summary>
    private readonly ChannelWriter<NetworkPacket> _incomingWriter;

    /// <summary>
    /// The port on which the QUIC listener operates.
    /// </summary>
    private readonly int _listeningPort;

    /// <summary>
    /// Logger for logging information and errors.
    /// </summary>
    private readonly ILogger<QuicListenerWorker> _logger;

    public QuicListenerWorker(
        Channel<NetworkPacket> channel,
        NodeSettings nodeSettings,
        ILogger<QuicListenerWorker> logger
    )
    {
        _incomingWriter = channel.Writer;
        _listeningPort = nodeSettings.Port;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("QUIC Listener Worker started.");

        // Simulation of listening loop.
        // In production, you would get the 'remoteEndPoint' from listener.AcceptConnectionAsync()
        while (!stoppingToken.IsCancellationRequested)
        {
            // TODO: Replace with actual QUIC listening logic
            // var connection = await listener.AcceptConnectionAsync(stoppingToken);
            // _ = ProcessConnectionAsync(stream, connection.RemoteEndPoint, stoppingToken);

            await Task.Delay(1000, stoppingToken);
        }
    }

    /// <summary>
    /// Processes an incoming QUIC connection by reading packets from the provided <paramref name="networkStream"/>.
    /// Each packet consists of a fixed-size header and a variable-size payload. The method enforces a maximum payload size limit,
    /// logs warnings for excessive payloads, and writes valid packets to the incoming writer channel.
    /// Buffers are rented from <see cref="ArrayPool{T}"/> for efficient memory usage and returned appropriately to avoid leaks.
    /// </summary>
    /// <param name="networkStream">The network stream representing the QUIC connection.</param>
    /// <param name="remoteEndPoint">The endpoint of the client that initiated the connection.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public async Task ProcessConnectionAsync(
        Stream networkStream,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken
    )
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(FixedHeader.Size);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Clear the buffer before reading, good for debugging
                Array.Clear(headerBuffer, 0, FixedHeader.Size);

                int bytesRead = await networkStream.ReadAtLeastAsync(
                    headerBuffer.AsMemory(0, FixedHeader.Size),
                    FixedHeader.Size,
                    cancellationToken: cancellationToken
                );

                if (bytesRead == 0)
                {
                    break; // Connection closed
                }

                var header = FixedHeader.ReadFromSpan(headerBuffer.AsSpan(0, FixedHeader.Size));

                if (header.PayloadLength > 1024 * 1024 * 5) // 5 MB limit
                {
                    _logger.LogWarning(
                        "Received packet with excessive payload length: {PayloadLength} bytes from {Remote}. Closing connection.",
                        header.PayloadLength,
                        remoteEndPoint
                    );
                    break; // Close connection on excessive payload
                }

                // Use the heap to allocate payload buffer, use ArrayPool for recycling
                byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent((int)header.PayloadLength);

                bool success = false;

                try
                {
                    await networkStream.ReadAtLeastAsync(
                        payloadBuffer.AsMemory(0, (int)header.PayloadLength),
                        (int)header.PayloadLength,
                        cancellationToken: cancellationToken
                    );

                    var packet = new NetworkPacket(
                        remoteEndPoint, // 1. Origin
                        header.MessageType, // 2. Type
                        header.RequestId, // 3. Request ID
                        payloadBuffer.AsMemory(0, (int)header.PayloadLength), // 4. Payload
                        payloadBuffer // 5. Original buffer reference
                    );

                    await this._incomingWriter.WriteAsync(packet, cancellationToken);
                    success = true;

                    // TODO: Process the payload as needed (handled by NodeLogicWorker via Channel)
                }
                finally
                {
                    // Only return the buffer if it wasn't passed along with the packet
                    if (!success)
                    {
                        // Return the rented buffer to the pool to avoid memory leaks
                        ArrayPool<byte>.Shared.Return(payloadBuffer);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing QUIC connection from {remoteEndPoint}.");
        }
        finally
        {
            // Return the header buffer to the pool
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }
}
