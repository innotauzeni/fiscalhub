using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fdms.Infrastructure.Fdms;

namespace Fdms.Tests
{
    public class DeviceSignerTests
    {
        private static (string KeyPem, string CertPem) SelfSigned(string cn = "CN=test")
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            return (rsa.ExportPkcs8PrivateKeyPem(), cert.ExportCertificatePem());
        }

        [Fact]
        public void Signs_and_hash_matches_sha256_base64()
        {
            var (key, cert) = SelfSigned();
            var res = new DeviceSigner().Sign("hello", key, cert);
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))), res.Hash);

            using var pub = X509Certificate2.CreateFromPem(cert).GetRSAPublicKey()!;
            Assert.True(pub.VerifyData(Encoding.UTF8.GetBytes("hello"), Convert.FromBase64String(res.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        [Fact]
        public void Mismatched_key_and_certificate_is_rejected()
        {
            var (key, _) = SelfSigned();
            var (_, otherCert) = SelfSigned();
            Assert.Throws<InvalidOperationException>(() => new DeviceSigner().Sign("x", key, otherCert));
        }

        [Fact]
        public void Csr_subject_follows_zimra_format()
        {
            var (keyPem, csrPem) = new CsrGenerator().Generate("SN-001", 187);
            Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", csrPem);
            Assert.StartsWith("-----BEGIN PRIVATE KEY-----", keyPem);
            var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default,
                RSASignaturePadding.Pkcs1);
            Assert.Equal("CN=ZIMRA-SN-001-0000000187", csr.SubjectName.Name);
        }
    }
}
