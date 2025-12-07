using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FalconNode.Core.Security;

/// <summary>
/// Provides helper methods for generating X509 certificates.
/// </summary>
public static class CertHelper
{
    /// <summary>
    /// Generates a self-signed X509 certificate using RSA with a 2048-bit key size and SHA256 hashing algorithm.
    /// The certificate is valid for 5 days from the time of creation and uses "CN=FreedomNode" as the subject name.
    /// </summary>
    /// <returns>
    /// A <see cref="X509Certificate2"/> instance representing the generated self-signed certificate.
    /// </returns>
    public static X509Certificate2 GenerateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=FreedomNode",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        DateTimeOffset now = DateTimeOffset.UtcNow;

        X509Certificate2 cert = request.CreateSelfSigned(now, now.AddDays(5));

        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }
}
