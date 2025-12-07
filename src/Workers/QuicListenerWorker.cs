using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
using FalconNode.Core.Security;
using FalconNode.Core.State;

namespace FalconNode.Workers;

/// <summary>
/// Background service that listens for incoming QUIC connections on a specified port,
/// processes inbound streams, and writes received network packets to a channel.
/// Handles connection lifecycle, error logging, and efficient buffer management.
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
    /// The QUIC listener instance.
    /// </summary>
    private QuicListener? _listener;

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
        try
        {
            var serverCertificate = CertHelper.GenerateSelfSignedCertificate();

            var listenerOptions = new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Any, _listeningPort),
                ApplicationProtocols = new List<SslApplicationProtocol> { new("freedom-v1") },
                ConnectionOptionsCallback = (connection, ssl, token) =>
                {
                    return ValueTask.FromResult(
                        new QuicServerConnectionOptions
                        {
                            DefaultStreamErrorCode = 0x04,
                            DefaultCloseErrorCode = 0x0B,
                            ServerAuthenticationOptions = new SslServerAuthenticationOptions
                            {
                                ServerCertificate = serverCertificate,
                                ApplicationProtocols = new List<SslApplicationProtocol>
                                {
                                    new("freedom-v1"),
                                },
                            },
                        }
                    );
                },
            };

            _listener = await QuicListener.ListenAsync(listenerOptions, stoppingToken);
            _logger.LogInformation($"QUIC Listener started on port {_listeningPort}.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var connection = await _listener.AcceptConnectionAsync(stoppingToken);
                _ = ProcessConnectionAsync(connection, stoppingToken);
            }
        }
        catch (QuicException ex)
            when (ex.InnerException is SocketException sockEx
                && sockEx.SocketErrorCode == SocketError.AddressAlreadyInUse
            )
        {
            // Handle port already in use error
            _logger.LogCritical(
                $"PORT {_listeningPort} IS ALREADY IN USE. Please close the other node or choose a different port (--port)."
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in QUIC Listener Worker.");
        }
        finally
        {
            if (_listener != null)
            {
                await _listener.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Processes an incoming QUIC connection by accepting an inbound stream and handling it.
    /// Logs the connection details and any errors encountered during processing.
    /// Ensures the connection is disposed after processing.
    /// </summary>
    /// <param name="connection">The <see cref="QuicConnection"/> representing the incoming QUIC connection.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ProcessConnectionAsync(
        QuicConnection connection,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var remoteEndPoint = connection.RemoteEndPoint;
            _logger.LogInformation($"Accepted QUIC connection from {remoteEndPoint}.");

            var stream = await connection.AcceptInboundStreamAsync(cancellationToken);

            await ProcessStreamAsync(stream, remoteEndPoint, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing QUIC connection.");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Asynchronously processes a network stream by reading fixed-size headers and payloads,
    /// constructing <see cref="NetworkPacket"/> instances, and writing them to the incoming packet writer.
    /// Utilizes array pooling for efficient buffer management and handles cancellation and error logging.
    /// </summary>
    /// <param name="networkStream">The network stream to read data from.</param>
    /// <param name="remoteEndPoint">The remote endpoint associated with the stream.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task ProcessStreamAsync(
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
                Array.Clear(headerBuffer, 0, FixedHeader.Size);
                int bytesRead = await networkStream.ReadAtLeastAsync(
                    headerBuffer.AsMemory(0, FixedHeader.Size),
                    FixedHeader.Size,
                    throwOnEndOfStream: false,
                    cancellationToken: cancellationToken
                );

                if (bytesRead == 0)
                {
                    // Stream finished gracefully
                    break;
                }

                if (bytesRead < FixedHeader.Size)
                {
                    _logger.LogWarning(
                        $"Connection closed with incomplete header from {remoteEndPoint}."
                    );
                    break;
                }

                var header = FixedHeader.ReadFromSpan(headerBuffer.AsSpan(0, FixedHeader.Size));

                if (header.PayloadLength > 1024 * 1024 * 5)
                {
                    break;
                }

                byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent((int)header.PayloadLength);

                bool success = false;

                try
                {
                    await networkStream.ReadAtLeastAsync(
                        payloadBuffer.AsMemory(0, (int)header.PayloadLength),
                        (int)header.PayloadLength,
                        throwOnEndOfStream: true,
                        cancellationToken: cancellationToken
                    );

                    var packet = new NetworkPacket(
                        remoteEndPoint,
                        header.MessageType,
                        header.RequestId,
                        payloadBuffer.AsMemory(0, (int)header.PayloadLength),
                        payloadBuffer
                    );

                    await _incomingWriter.WriteAsync(packet, cancellationToken);
                    success = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, $"Error reading payload from {remoteEndPoint}.");
                }
                finally
                {
                    if (!success)
                    {
                        ArrayPool<byte>.Shared.Return(payloadBuffer);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing QUIC stream.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }
}
