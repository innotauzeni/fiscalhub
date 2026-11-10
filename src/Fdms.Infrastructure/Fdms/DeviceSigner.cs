using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;

namespace Fdms.Infrastructure.Fdms
{
    /// <summary>
    /// SHA-256 hash + RSA PKCS#1 v1.5 signature (spec 13.1). Self-verifies against the device certificate so a
    /// wrong key/cert pair fails here instead of surfacing as RCPT020 at ZIMRA.
    /// </summary>
    public sealed class DeviceSigner : IDeviceSigner
    {
        public SignatureResult Sign(string dataToSign, string privateKeyPem, string certificatePem)
        {
            var bytes = Encoding.UTF8.GetBytes(dataToSign);
            var hash = SHA256.HashData(bytes);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var signature = rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            using var cert = X509Certificate2.CreateFromPem(certificatePem);
            using var pub = cert.GetRSAPublicKey()
                ?? throw new InvalidOperationException("Device certificate does not contain an RSA public key.");
            if (!pub.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidOperationException(
                    "Device private key does not match the device certificate (would be rejected as RCPT020). Re-register the device.");

            return new SignatureResult(Convert.ToBase64String(hash), Convert.ToBase64String(signature));
        }
    }

    /// <summary>RSA-2048 key + PKCS#10 CSR, CN = ZIMRA-&lt;serial&gt;-&lt;10 digit deviceId&gt; (spec 4.2).</summary>
    public sealed class CsrGenerator : ICsrGenerator
    {
        public (string PrivateKeyPem, string CsrPem) Generate(string deviceSerialNo, int deviceId)
        {
            using var rsa = RSA.Create(2048);
            var cn = $"ZIMRA-{deviceSerialNo}-{deviceId.ToString().PadLeft(10, '0')}";
            var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var csr = request.CreateSigningRequestPem().Replace("\r\n", "\n").Trim();
            var key = rsa.ExportPkcs8PrivateKeyPem().Replace("\r\n", "\n").Trim();
            return (key, csr);
        }
    }
}
