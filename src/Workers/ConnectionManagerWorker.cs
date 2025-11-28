using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading.Channels;
using FalconNode.Core.Messages;

namespace FalconNode.Workers;

/// <summary>
/// Worker responsible for managing QUIC connections and sending outgoing messages.
/// </summary>
public class ConnectionManagerWorker : BackgroundService
{
    private readonly ChannelReader<OutgoingMessage> _outReader;
    private readonly Dictionary<IPEndPoint, QuicConnection> _activeConnections = new();

    public ConnectionManagerWorker(Channel<OutgoingMessage> outChannel)
    {
        _outReader = outChannel.Reader;
    }

    
    /// <summary>
    /// Executes the main loop of the Connection Manager Worker, processing outgoing messages and managing QUIC connections.
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in _outReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                QuicConnection connection = await GetConnectionAsync(msg.Target, stoppingToken);

                await using var stream = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    stoppingToken
                );

                await stream.WriteAsync(msg.Payload, stoppingToken);

                // await stream.ShutdownAsync();
            }
            catch (Exception ex)
            {
                _activeConnections.Remove(msg.Target);
                Console.WriteLine($"Error sending message to {msg.Target}: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(msg.OriginalBufferReference);
            }
        }
    }

    /// <summary>
    /// Retrieves an active <see cref="QuicConnection"/> to the specified <paramref name="target"/> endpoint.
    /// If a valid connection already exists, it is returned; otherwise, a new connection is established.
    /// </summary>
    /// <param name="target">The <see cref="IPEndPoint"/> representing the remote endpoint to connect to.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> to observe while waiting for the connection.</param>
    /// <returns>
    /// A <see cref="ValueTask{QuicConnection}"/> representing the asynchronous operation to retrieve or establish the connection.
    /// </returns>
    private async ValueTask<QuicConnection> GetConnectionAsync(
        IPEndPoint target,
        CancellationToken cancellationToken
    )
    {
        if (_activeConnections.TryGetValue(target, out var conn))
        {
            try
            {
                if (conn.RemoteEndPoint != null)
                    return conn;
            }
            catch (Exception ex)
            {
                _activeConnections.Remove(target);
            }
        }

        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = target,
            DefaultStreamErrorCode = 0x100,
            DefaultCloseErrorCode = 0x200,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { new("freedom-v1") },
                RemoteCertificateValidationCallback = (
                    sender,
                    certificate,
                    chain,
                    sslPolicyErrors
                ) => true,
            },
        };

        var newConn = await QuicConnection.ConnectAsync(clientOptions, cancellationToken);
        _activeConnections[target] = newConn;
        return newConn;
    }
}
