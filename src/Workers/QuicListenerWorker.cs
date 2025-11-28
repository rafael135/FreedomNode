using System.Buffers;
using System.Net.Quic;
using System.Threading.Channels;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;

namespace FalconNode.Workers;


/// <summary>
/// Worker responsible for listening for incoming QUIC connections and processing incoming network packets.
/// </summary>
public class QuicListenerWorker : BackgroundService
{
    private readonly ChannelWriter<NetworkPacket> _incomingWriter;
    private readonly ILogger<QuicListenerWorker> _logger;

    public QuicListenerWorker(Channel<NetworkPacket> channel, ILogger<QuicListenerWorker> logger)
    {
        _incomingWriter = channel.Writer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("QUIC Listener Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Simulate receiving a packet for demonstration purposes
            //await this._incomingWriter.WriteAsync(packet, stoppingToken);

            // TODO: Replace with actual QUIC listening and packet receiving logic
            // TODO: Process incoming QUIC connections and read packets

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
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ProcessConnectionAsync(
        Stream networkStream,
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
                    FixedHeader.Size
                );
                if (bytesRead == 0)
                {
                    break; // Connection closed
                }

                var header = FixedHeader.ReadFromSpan(headerBuffer.AsSpan(0, FixedHeader.Size));

                if (header.PayloadLength > 1024 * 1024 * 5) // 5 MB limit
                {
                    _logger.LogWarning(
                        "Received packet with excessive payload length: {PayloadLength} bytes. Closing connection.",
                        header.PayloadLength
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
                        header.MessageType,
                        header.RequestId,
                        payloadBuffer.AsMemory(0, (int)header.PayloadLength),
                        payloadBuffer
                    );

                    await this._incomingWriter.WriteAsync(packet, cancellationToken);
                    success = true;

                    // TODO: Process the payload as needed
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
            _logger.LogError(ex, "Error processing QUIC connection.");
        }
        finally
        {
            // Return the header buffer to the pool
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }
}
