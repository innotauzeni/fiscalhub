using Fdms.Application.Fiscal;

namespace Fdms.Tests
{
    public class QrCodeTests
    {
        [Fact]
        public void Qr_url_matches_spec_layout()
        {
            var sig = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
            var qr = QrCodeBuilder.Build("https://invoice.zimra.co.zw/", 321, new DateTime(2023, 4, 3), 1112223331, sig);
            Assert.StartsWith("https://invoice.zimra.co.zw/0000000321" + "03042023" + "1112223331", qr);
            Assert.Equal("https://invoice.zimra.co.zw/".Length + 10 + 8 + 10 + 16, qr.Length);
            Assert.Equal(qr.ToUpperInvariant()[^16..], qr[^16..]); // uppercase hex
        }

        [Fact]
        public void Global_no_and_device_id_are_zero_padded()
        {
            var qr = QrCodeBuilder.Build("https://x.test", 5, new DateTime(2024, 12, 1), 7, Convert.ToBase64String(new byte[] { 9 }));
            Assert.StartsWith("https://x.test/0000000005" + "01122024" + "0000000007", qr);
        }
    }
}
