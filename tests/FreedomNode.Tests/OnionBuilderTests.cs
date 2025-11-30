using System.Net;
using FalconNode.Core.Crypto;
using FalconNode.Core.State;
using NSec.Cryptography;

namespace FreedomNode.Tests;

public class OnionBuilderTests
{
    [Fact]
    public void BuildSingleHop_Onion_DecryptsAtHop()
    {
        // Simulate a single hop route
        var nodeKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var nodePublicBytes = nodeKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var route = new List<RouteHop>
        {
            new RouteHop(new IPEndPoint(IPAddress.Loopback, 20000), nodePublicBytes)
        };

        var clientEphemeral = Key.Create(KeyAgreementAlgorithm.X25519);

        var finalMessage = System.Text.Encoding.UTF8.GetBytes("hello onion");

        byte[] onionPacket = OnionBuilder.Build(finalMessage, route, clientEphemeral);

        // The runtime wire format used by NodeLogicWorker expects: [32 bytes sender ephemeral key] + encrypted onion bytes
        var senderPub = clientEphemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        // Simulate receiving side: derive same session key and decrypt
        using var sessionForNode = CryptoHelper.CreateSessionKey(nodeKey, PublicKey.Import(KeyAgreementAlgorithm.X25519, senderPub, KeyBlobFormat.RawPublicKey));

        int expectedPlainSize = 1 + finalMessage.Length;
        byte[] decrypted = new byte[expectedPlainSize];
        bool ok = CryptoHelper.TryDecryptLayer(sessionForNode, onionPacket, decrypted);

        Assert.True(ok);

        // First byte should be final message indicator (0x00) and rest the final message
        Assert.Equal((byte)0x00, decrypted[0]);
        Assert.Equal(finalMessage, decrypted.AsSpan(1).ToArray());
    }
}
