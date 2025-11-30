using System.Net;

namespace FalconNode.Core.State;

/// <summary>
/// Represents a single hop in a network route, containing the endpoint and onion public key information.
/// </summary>
public record RouteHop
{
    /// <summary>
    /// The network endpoint of the hop.
    /// </summary>
    public IPEndPoint Endpoint { get; init; }

    /// <summary>
    /// The onion public key of the hop.
    /// </summary>
    public byte[] OnionKey { get; init; } // X25519 Puhblic Key

    /// <summary>
    /// Initializes a new instance of the <see cref="RouteHop"/> record with the specified endpoint and onion key.
    /// </summary>
    /// <param name="endpoint">The network endpoint of the hop.</param>
    /// <param name="onionKey">The onion public key of the hop (X25519 public key).</param>
    public RouteHop(IPEndPoint endpoint, byte[] onionKey)
    {
        Endpoint = endpoint;
        OnionKey = onionKey;
    }
}
