using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fdms.Application.Fdms
{
    public enum FdmsEnvironment { Test = 0, Production = 1 }

    /// <summary>Everything needed to talk to FDMS as one device. Key material is held in memory only.</summary>
    public sealed record FdmsDeviceCredentials(
        FdmsEnvironment Environment,
        int DeviceId,
        string ModelName,
        string ModelVersion,
        string CertificatePem,
        string PrivateKeyPem);

    // ---- responses -------------------------------------------------------------------------------------------

    public sealed record FdmsTax(int TaxId, decimal? TaxPercent, string TaxName, DateTime TaxValidFrom, DateTime? TaxValidTill);

    public sealed record FdmsAddress(string? Province, string? City, string? Street, string? HouseNo);

    public sealed record FdmsContacts(string? PhoneNo, string? Email);

    public sealed record VerifyTaxpayerResponse(
        string OperationId, string TaxPayerName, string TaxPayerTin, string? VatNumber,
        string DeviceBranchName, FdmsAddress? DeviceBranchAddress, FdmsContacts? DeviceBranchContacts);

    public sealed record RegisterDeviceResponse(string OperationId, string Certificate);

    public sealed record FdmsConfig(
        string OperationId, string TaxPayerName, string TaxPayerTin, string? VatNumber, string DeviceSerialNo,
        string DeviceBranchName, FdmsAddress? DeviceBranchAddress, FdmsContacts? DeviceBranchContacts,
        string DeviceOperatingMode, int TaxPayerDayMaxHrs, int TaxpayerDayEndNotificationHrs,
        List<FdmsTax> ApplicableTaxes, DateTime CertificateValidTill, string QrUrl);

    public sealed record FdmsStatus(
        string OperationId, string FiscalDayStatus, string? FiscalDayReconciliationMode, DateTime? FiscalDayClosed,
        string? FiscalDayClosingErrorCode, int? LastFiscalDayNo, long? LastReceiptGlobalNo);

    public sealed record OpenDayResponse(string OperationId, int FiscalDayNo);

    public sealed record FdmsValidationError(string ValidationErrorCode, string ValidationErrorColor);

    public sealed record SubmitReceiptResponse(
        string OperationId, long ReceiptId, DateTime ServerDate, JsonElement ReceiptServerSignature,
        List<FdmsValidationError>? ValidationErrors);

    public sealed record CloseDayResponse(string OperationId);

    public sealed record PingResponse(string OperationId, int? ReportingFrequency);

    public sealed record ServerCertificateResponse(List<string> Certificate, DateTime? CertificateValidTill);

    public sealed record StockGood(string HsCode, string GoodName, decimal Quantity, int TaxPayerId, string? TaxPayerName, int? BranchId, string? BranchName);

    public sealed record StockListResponse(int Total, List<StockGood> Rows);

    public sealed record SubmitFileResponse(string OperationId);

    public sealed record FileStatusItem(string OperationId, DateTime? FileUploadDate, int? DeviceId, string? FileName, DateTime? FileProcessingDate,
        string FileProcessingStatus, List<string>? FileProcessingErrorCode, int? FiscalDayNo, int? FileSequence);

    public sealed record FileStatusResponse(string OperationId, List<FileStatusItem> FileStatus);

    // ---- offline file (spec 4.9.1) ----
    public sealed record FileHeaderDto([property: JsonPropertyName("deviceID")] int DeviceId, int FiscalDayNo, string FiscalDayOpened, int FileSequence);
    public sealed record FileContentDto(List<ReceiptPayload> Receipts);
    public sealed record FileFooterDto(List<FiscalCounterDto> FiscalDayCounters, SignatureDto FiscalDayDeviceSignature, int ReceiptCounter, string FiscalDayClosed);
    public sealed record FiscalDayFileDto(FileHeaderDto Header, FileContentDto? Content, FileFooterDto? Footer);

    // ---- requests --------------------------------------------------------------------------------------------

    public sealed record SignatureDto(string Hash, string Signature);

    public sealed record BuyerDto(string BuyerRegisterName, string? BuyerTradeName, [property: JsonPropertyName("buyerTIN")] string BuyerTin,
        [property: JsonPropertyName("VATNumber")] string? VatNumber,
        FdmsContacts? BuyerContacts, FdmsAddress? BuyerAddress);

    public sealed record CreditDebitNoteDto(
        [property: JsonPropertyName("receiptID")] long? ReceiptId,
        [property: JsonPropertyName("deviceID")] int? DeviceId, long? ReceiptGlobalNo, int? FiscalDayNo, string? ReceiptDate = null);

    public sealed record ReceiptLineDto(string ReceiptLineType, int ReceiptLineNo, string? ReceiptLineHSCode, string ReceiptLineName,
        decimal ReceiptLinePrice, decimal ReceiptLineQuantity, decimal ReceiptLineTotal, string? TaxCode, decimal? TaxPercent,
        [property: JsonPropertyName("taxID")] int TaxId);

    public sealed record ReceiptTaxDto(string? TaxCode, decimal? TaxPercent, [property: JsonPropertyName("taxID")] int TaxId,
        decimal TaxAmount, decimal SalesAmountWithTax);

    public sealed record ReceiptPaymentDto(string MoneyTypeCode, decimal PaymentAmount);

    public sealed record ReceiptPayload(
        string ReceiptType, string ReceiptCurrency, int ReceiptCounter, long ReceiptGlobalNo, string InvoiceNo,
        BuyerDto? BuyerData, string? ReceiptNotes, string ReceiptDate, CreditDebitNoteDto? CreditDebitNote,
        bool ReceiptLinesTaxInclusive, List<ReceiptLineDto> ReceiptLines, List<ReceiptTaxDto> ReceiptTaxes,
        List<ReceiptPaymentDto> ReceiptPayments, decimal ReceiptTotal, string ReceiptPrintForm,
        SignatureDto ReceiptDeviceSignature, string? Username, string? UserNameSurname);

    public sealed record FiscalCounterDto(string FiscalCounterType, string FiscalCounterCurrency, decimal? FiscalCounterTaxPercent,
        int? FiscalCounterTaxID, string? FiscalCounterMoneyType, decimal FiscalCounterValue);

    public sealed record CloseDayRequest(int FiscalDayNo, List<FiscalCounterDto> FiscalDayCounters,
        SignatureDto FiscalDayDeviceSignature, int ReceiptCounter);

    // ---- gateway ---------------------------------------------------------------------------------------------

    /// <summary>Raised for any non-2xx FDMS answer. ErrorCode is the RFC 7807 errorCode (DEVxx, RCPTxx, FISCxx, FILExx).</summary>
    public sealed class FdmsException : Exception
    {
        public int HttpStatus { get; }
        public string? ErrorCode { get; }
        public string? RawBody { get; }

        /// <summary>True when the request may be retried later (network error, 500, 502) — spec section 8.1.</summary>
        public bool IsTransient => HttpStatus is 0 or 500 or 502 or 503 or 504;

        public FdmsException(int httpStatus, string? errorCode, string message, string? rawBody = null, Exception? inner = null)
            : base(message, inner)
        {
            HttpStatus = httpStatus;
            ErrorCode = errorCode;
            RawBody = rawBody;
        }
    }

    public interface IFdmsGateway
    {
        // public (no client certificate)
        Task<VerifyTaxpayerResponse> VerifyTaxpayerAsync(FdmsEnvironment env, int deviceId, string activationKey, string deviceSerialNo,
            string modelName, string modelVersion, CancellationToken ct = default);

        Task<RegisterDeviceResponse> RegisterDeviceAsync(FdmsEnvironment env, int deviceId, string activationKey, string csrPem,
            string modelName, string modelVersion, CancellationToken ct = default);

        // device (mutual TLS)
        Task<RegisterDeviceResponse> IssueCertificateAsync(FdmsDeviceCredentials d, string csrPem, CancellationToken ct = default);
        Task<FdmsConfig> GetConfigAsync(FdmsDeviceCredentials d, CancellationToken ct = default);
        Task<FdmsStatus> GetStatusAsync(FdmsDeviceCredentials d, CancellationToken ct = default);
        Task<OpenDayResponse> OpenDayAsync(FdmsDeviceCredentials d, DateTime fiscalDayOpened, int? fiscalDayNo, CancellationToken ct = default);
        Task<SubmitReceiptResponse> SubmitReceiptAsync(FdmsDeviceCredentials d, ReceiptPayload receipt, CancellationToken ct = default);
        Task<CloseDayResponse> CloseDayAsync(FdmsDeviceCredentials d, CloseDayRequest request, CancellationToken ct = default);

        /// <summary>Drops the cached mTLS client for a device (call after certificate renewal / re-registration).</summary>
        Task<PingResponse> PingAsync(FdmsDeviceCredentials d, CancellationToken ct = default);
        Task<StockListResponse> GetStockListAsync(FdmsDeviceCredentials d, string? hsCode, string? goodName, int offset, int limit, CancellationToken ct = default);
        Task<ServerCertificateResponse> GetServerCertificateAsync(FdmsEnvironment env, string? thumbprint, string modelName, string modelVersion, CancellationToken ct = default);
        /// <summary>Offline mode: uploads one fiscal-day file (base64 JSON text) as multipart/form-data.</summary>
        Task<SubmitFileResponse> SubmitFileAsync(FdmsDeviceCredentials d, string fileName, string base64Content, CancellationToken ct = default);
        Task<FileStatusResponse> GetFileStatusAsync(FdmsDeviceCredentials d, DateTime uploadedFrom, DateTime uploadedTill, string? operationId, CancellationToken ct = default);

        void Invalidate(int deviceId);
    }

    /// <summary>Hash + RSA signature of the exact strings from <c>SignatureStringBuilder</c>.</summary>
    public interface IDeviceSigner
    {
        Fiscal.SignatureResult Sign(string dataToSign, string privateKeyPem, string certificatePem);
    }

    /// <summary>Generates a key pair and CSR for registerDevice / issueCertificate.</summary>
    public interface ICsrGenerator
    {
        (string PrivateKeyPem, string CsrPem) Generate(string deviceSerialNo, int deviceId);
    }

    public static class FdmsJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new FlexibleDateTimeConverter() }
        };

        /// <summary>FDMS sends local-time ISO strings with or without fractional seconds / zone; keep them as written.</summary>
        private sealed class FlexibleDateTimeConverter : JsonConverter<DateTime>
        {
            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                DateTime.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind);

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
                writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
