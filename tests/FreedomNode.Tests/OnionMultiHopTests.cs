using System.Net;
using NSec.Cryptography;
using FalconNode.Core.Crypto;
using FalconNode.Core.State;

namespace FreedomNode.Tests;

public class OnionMultiHopTests
{
    [Fact]
    public void MultiHopOnion_Peeling_Works()
    {
        // Create per-hop keypairs (3 hops)
        var hopKeys = new[]
        {
            Key.Create(KeyAgreementAlgorithm.X25519),
            Key.Create(KeyAgreementAlgorithm.X25519),
            Key.Create(KeyAgreementAlgorithm.X25519),
        };

        var route = new List<RouteHop>();

        for (int i = 0; i < hopKeys.Length; i++)
        {
            var pub = hopKeys[i].PublicKey.Export(KeyBlobFormat.RawPublicKey);
            route.Add(new RouteHop(new IPEndPoint(IPAddress.Loopback, 20000 + i), pub));
        }

        // Client ephemeral
        var clientEphemeral = Key.Create(KeyAgreementAlgorithm.X25519);

        var finalMessage = System.Text.Encoding.UTF8.GetBytes("final content for multi-hop");

        // Build the onion payload
        byte[] onionPacket = OnionBuilder.Build(finalMessage, route, clientEphemeral);

        // Wire format: [32 bytes sender ephemeral public key] + encrypted onion bytes
        var senderPub = clientEphemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        byte[] wire = new byte[senderPub.Length + onionPacket.Length];
        senderPub.CopyTo(wire.AsSpan(0, senderPub.Length));
        onionPacket.CopyTo(wire.AsSpan(senderPub.Length));

        // Peel each hop sequentially
        ReadOnlySpan<byte> rem = wire;
        byte[] peeled;

        for (int hopIdx = 0; hopIdx < hopKeys.Length; hopIdx++)
        {
            // first 32 bytes are sender ephemeral key
            var senderEphemeral = rem.Slice(0, 32).ToArray();
            var encryptedLayer = rem.Slice(32);

            // Derive session key on hop (hop private + sender public)
            using Key sessionKey = CryptoHelper.CreateSessionKey(hopKeys[hopIdx], PublicKey.Import(KeyAgreementAlgorithm.X25519, senderEphemeral, KeyBlobFormat.RawPublicKey));

            // Decrypt layer
            int plainSize = encryptedLayer.Length - 28; // remove overhead
            peeled = new byte[plainSize];

            bool ok = CryptoHelper.TryDecryptLayer(sessionKey, encryptedLayer, peeled);
            Assert.True(ok, "Layer decrypt failed");

            // First hop may be relay (0x01) or final (0x00)
            byte cmd = peeled[0];
            if (hopIdx < hopKeys.Length - 1)
            {
                // Expect relay
                Assert.Equal((byte)0x01, cmd);
                // Relay structure: [0x01][ipLen][ipBytes][port(2)][nextPayload]
                int ipLen = peeled[1];
                int headerLen = 1 + 1 + ipLen + 2;
                Assert.True(peeled.Length > headerLen, "Relay payload missing next layer");

                // The next payload is the remainder and should itself be an encrypted blob for the next hop.
                var nextPayload = peeled.AsSpan(headerLen);

                // Build new wire: keep same sender ephemeral key but replace encrypted layer with nextPayload
                // In real protocol the next hop would receive header+nextPayload; tests simulate peeling chain by setting rem to [senderPub | nextPayload]
                byte[] nextWire = new byte[senderPub.Length + nextPayload.Length];
                senderPub.CopyTo(nextWire.AsSpan(0, senderPub.Length));
                nextPayload.CopyTo(nextWire.AsSpan(senderPub.Length));
                rem = nextWire;
            }
            else
            {
                // Last hop -> final message indicator 0x00
                Assert.Equal((byte)0x00, cmd);
                var final = peeled.AsSpan(1).ToArray();
                Assert.Equal(finalMessage, final);
            }
        }
    }
}
