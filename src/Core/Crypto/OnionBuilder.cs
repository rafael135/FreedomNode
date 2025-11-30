using System.Buffers;
using System.Buffers.Binary;
using FalconNode.Core.State;
using NSec.Cryptography;

namespace FalconNode.Core.Crypto;


/// <summary>
/// Provides functionality to construct onion-encrypted packets for multi-hop routing.
/// </summary>
/// <remarks>
/// The <see cref="OnionBuilder"/> class builds an onion packet by wrapping a final message in multiple encryption layers,
/// each corresponding to a hop in the provided route. The packet is constructed in reverse order, starting from the last hop
/// and proceeding to the first, using the client's ephemeral key for key agreement at each hop. The resulting packet can be sent
/// to the first hop, which will decrypt its layer and forward the payload to the next hop.
/// </remarks>
public static class OnionBuilder
{
    /// <summary>
    /// Builds an onion-encrypted packet by wrapping the <paramref name="finalMessage"/> in multiple encryption layers,
    /// each corresponding to a hop in the provided <paramref name="route"/>. The packet is constructed in reverse order,
    /// starting from the last hop and proceeding to the first, using the client's ephemeral key for key agreement at each hop.
    /// The resulting packet can be sent to the first hop, which will decrypt its layer and forward the payload to the next hop.
    /// </summary>
    /// <param name="finalMessage">
    /// The final message to be delivered to the last hop, as a read-only byte span.
    /// </param>
    /// <param name="route">
    /// The ordered list of <see cref="RouteHop"/> objects representing the hops in the route. The first element is the entry hop,
    /// and the last is the final destination.
    /// </param>
    /// <param name="clientEphemeralKey">
    /// The client's ephemeral key used for key agreement with each hop's public key.
    /// </param>
    /// <returns>
    /// A byte array containing the fully wrapped onion packet, encrypted for each hop in the route.
    /// </returns>
    public static byte[] Build(
        ReadOnlySpan<byte> finalMessage,
        List<RouteHop> route,
        Key clientEphemeralKey
    )
    {
        // 1. Start with the final message
        byte[] currentPayload = new byte[1 + finalMessage.Length];
        currentPayload[0] = 0x00; // Final message indicator
        finalMessage.CopyTo(currentPayload.AsSpan(1));


        // 2. Reverse loop: build layers from last hop to first
        for (int i = route.Count - 1; i >= 0; i--)
        {
            var hop = route[i];

            // A. Derive shared session key for this hop
            // The client uses its ephemeral private key and the hop's public key
            var algorithm = KeyAgreementAlgorithm.X25519;
            var nodePublicKey = PublicKey.Import(
                algorithm,
                hop.OnionKey,
                KeyBlobFormat.RawPublicKey
            );
            var sessionKey = CryptoHelper.CreateSessionKey(clientEphemeralKey, nodePublicKey);

            // B. Prepare this layer's content
            // If its not the last hop, include next hop info
            // [CMD 0x01(RELAY)] + [Next IP] + [Next Port] + [Last crypted payload]
            byte[] layerContent;

            if (i == route.Count - 1)
            {
                // last hop, just wrap the current payload
                layerContent = currentPayload;
            }
            else
            {
                // Intermediate hop, include next hop info
                var nextHop = route[i + 1];

                // Serialize IP(4 bytes or 16 bytes)
                byte[] addressBytes = nextHop.Endpoint.Address.GetAddressBytes();
                byte ipLen = (byte)addressBytes.Length;

                // Calculate layer size and allocate buffer
                // 1 (CMD) + 1 (IP Length) + IP + 2 (Port) + Last Payload
                int layerSize = 1 + 1 + ipLen + 2 + currentPayload.Length;
                layerContent = new byte[layerSize];

                var span = layerContent.AsSpan();
                span[0] = 0x01; // Relay indicator
                span[1] = ipLen; // IP length
                addressBytes.CopyTo(span.Slice(2));
                BinaryPrimitives.WriteUInt16BigEndian(
                    span.Slice(2 + ipLen, 2),
                    (ushort)nextHop.Endpoint.Port
                );

                // Copy the crypted payload for the previous hop
                currentPayload.CopyTo(span.Slice(2 + ipLen + 2));
            }

            // C. Encrypt this layer
            // The result(CipherText + Nonce + Tag) becomes the payload for the next iteration(more outer layer)
            // Extra size: 12 (Nonce) + 16 (Tag) = 28 bytes
            byte[] encryptedLayer = new byte[layerContent.Length + 28];

            CryptoHelper.EncryptLayer(sessionKey, layerContent, encryptedLayer);

            // Update current payload for next iteration
            currentPayload = encryptedLayer;
        }

        // 3. Return the fully wrapped onion packet
        // Complete onion packet to send to the first hop. A -> B -> C
        return currentPayload;
    }
}
