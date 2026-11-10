using System.Globalization;
using System.Security.Cryptography;

namespace Fdms.Application.Fiscal
{
    /// <summary>Receipt QR deep link (spec section 11).</summary>
    public static class QrCodeBuilder
    {
        /// <param name="signatureBase64">receiptDeviceSignature.signature (base64).</param>
        public static string Build(string qrUrl, int deviceId, DateTime receiptDate, long globalNo, string signatureBase64)
        {
            var md5 = Convert.ToHexString(MD5.HashData(Convert.FromBase64String(signatureBase64)));
            return qrUrl.TrimEnd('/') + "/" +
                   deviceId.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0') +
                   receiptDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture) +
                   globalNo.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0') +
                   md5[..16];
        }
    }
}
