using Fdms.Application.Fdms;
using Fdms.Domain.Enums;

namespace Fdms.Application.Abstractions
{
    /// <summary>Business-rule failure; the API turns it into an RFC 7807 problem response.</summary>
    public sealed class GatewayException : Exception
    {
        public int Status { get; }
        public string Code { get; }
        public GatewayException(int status, string code, string message) : base(message) { Status = status; Code = code; }

        public static GatewayException NotFound(string what) => new(404, "NOT_FOUND", $"{what} not found.");
        public static GatewayException Conflict(string code, string msg) => new(409, code, msg);
        public static GatewayException BadRequest(string code, string msg) => new(400, code, msg);
        public static GatewayException Unprocessable(string code, string msg) => new(422, code, msg);
    }

    // ---- DTOs ------------------------------------------------------------------------------------------------

    public sealed record DeviceIdentityRequest(int ZimraDeviceId, string ActivationKey, string SerialNo,
        string ModelName, string ModelVersion, FdmsEnvironment Environment = FdmsEnvironment.Test);

    public sealed record DeviceTaxDto(int TaxId, decimal? TaxPercent, string TaxName, DateTime ValidFrom, DateTime? ValidTill);

    public sealed record DeviceDto(
        Guid Id, int ZimraDeviceId, FdmsEnvironment Environment, string SerialNo, string? TaxPayerName, string? TaxPayerTin,
        string? VatNumber, string? BranchName, DeviceOperatingMode OperatingMode, DateTime? CertificateValidTill,
        string? QrUrl, bool IsActive, IReadOnlyList<DeviceTaxDto> Taxes, Guid? BranchId = null, string? AssignedBranch = null, string? Name = null);

    public sealed record FiscalCounterView(string Type, string Currency, int? TaxId, decimal? TaxPercent, string? MoneyType, decimal Value);

    public sealed record FiscalDayDto(
        Guid DeviceId, int FiscalDayNo, string Status, DateTime OpenedAt, DateTime? ClosedAt, int LastReceiptCounter,
        string? ClosingErrorCode, IReadOnlyList<FiscalCounterView> Counters);

    /// <summary>Give the FDMS <c>taxId</c>, OR a <c>taxPercent</c> (15, 15.5, 0), OR <c>isExempt</c>; the gateway maps it to the device tax.</summary>
    public sealed record ReceiptLineRequest(string Name, string? HsCode, decimal Quantity, decimal UnitPrice,
        int? TaxId = null, decimal? TaxPercent = null, bool IsExempt = false, bool IsDiscount = false);

    public sealed record ReceiptPaymentRequest(MoneyType MoneyType, decimal Amount);

    public sealed record BuyerRequest(string RegisterName, string? TradeName, string Tin, string? VatNumber, string? Email, string? Phone);

    public sealed record SubmitReceiptRequest(
        string ExternalRef,
        string InvoiceNo,
        ReceiptType ReceiptType,
        string Currency,
        bool LinesTaxInclusive,
        IReadOnlyList<ReceiptLineRequest> Lines,
        IReadOnlyList<ReceiptPaymentRequest>? Payments = null,
        BuyerRequest? Buyer = null,
        string? Notes = null,
        /// <summary>Gateway receipt id of the invoice being credited / debited (required for credit and debit notes).</summary>
        Guid? OriginalReceiptId = null,
        string? Username = null,
        /// <summary>Open a fiscal day automatically if none is open.</summary>
        bool AutoOpenDay = true,
        /// <summary>
        /// Grace period (0-60 min) during which the receipt is validated but NOT numbered or sent, so it can still be cancelled with
        /// no trace at ZIMRA. When it ends the receipt is issued automatically (or release it now).
        /// </summary>
        int HoldMinutes = 0);

    public sealed record ValidationErrorView(string Code, string Color);

    public sealed record ReceiptDto(
        Guid Id, Guid DeviceId, string ExternalRef, string InvoiceNo, string Type, string Currency, string Status,
        int FiscalDayNo, int ReceiptCounter, long ReceiptGlobalNo, DateTime ReceiptDate, decimal Total,
        long? FdmsReceiptId, DateTime? ServerDate, string? QrUrl, string DeviceSignatureHash,
        IReadOnlyList<ValidationErrorView> ValidationErrors, string? ErrorCode, string? ErrorMessage, DateTime? HoldUntil = null);

    /// <summary>A receipt with what was actually sent to ZIMRA (lines, payments, buyer), so it can be reviewed and corrected.</summary>
    public sealed record ReceiptDetailDto(ReceiptDto Receipt, IReadOnlyList<global::Fdms.Application.Fdms.ReceiptLineDto> Lines, IReadOnlyList<global::Fdms.Application.Fdms.ReceiptPaymentDto> Payments, bool LinesTaxInclusive,
        string? Notes, global::Fdms.Application.Fdms.BuyerDto? Buyer, Guid? CreditNoteId);

    public sealed record LineFix(int LineNo, string? HsCode);

    /// <summary>
    /// Correct a receipt that was already fiscalised: a credit note reverses it, and (optionally) a corrected invoice replaces it. Fixes replace
    /// the HS code of the listed lines; every other line is copied exactly.
    /// </summary>
    public sealed record CorrectReceiptRequest(string Reason, IReadOnlyList<LineFix>? Fixes, bool IssueReplacement, string? ReplacementInvoiceNo);

    public sealed record CorrectionResult(ReceiptDto CreditNote, ReceiptDto? Replacement);

    /// <summary>
    /// A debit note adds to an invoice already accepted by ZIMRA (an undercharge, extra freight, interest): the lines are the ADDITIONAL charges. Tax and currency follow the
    /// original invoice. MoneyType is how the extra amount is paid (cash when not given).
    /// </summary>
    public sealed record DebitNoteRequest(string Reason, IReadOnlyList<ReceiptLineRequest> Lines, MoneyType? MoneyType = null, string? InvoiceNo = null);

    public sealed record OfflineFileDto(int Id, int FiscalDayNo, int Sequence, int ReceiptCount, bool HasFooter, string Status, string? OperationId,
        IReadOnlyList<string> Errors, string? LastError, int Attempts, DateTime? UploadedAt, DateTime? ProcessedAt);

    public sealed record WebhookConfigDto(string? Url, string? Secret);

    public sealed record WebhookDeliveryDto(Guid Id, string EventType, string Status, int Attempts, DateTime NextAttemptAt,
        DateTime? DeliveredAt, string? LastError, DateTime CreatedAt);

    // ---- services (implemented in Infrastructure) ---------------------------------------------------------------



    public interface IDeviceService
    {
        Task<VerifyTaxpayerResponse> VerifyAsync(DeviceIdentityRequest req, CancellationToken ct);
        Task<DeviceDto> RegisterAsync(Guid clientId, DeviceIdentityRequest req, CancellationToken ct);
        Task<IReadOnlyList<DeviceDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<DeviceDto> GetAsync(Guid clientId, Guid deviceId, CancellationToken ct);
        Task<DeviceDto> RefreshConfigAsync(Guid clientId, Guid deviceId, CancellationToken ct);
        /// <summary>Gives the machine the company's own name (empty clears it).</summary>
        Task<DeviceDto> RenameAsync(Guid clientId, Guid deviceId, string? name, CancellationToken ct);
        Task<DeviceDto> RenewCertificateAsync(Guid clientId, Guid deviceId, CancellationToken ct);
        /// <summary>FDMS reachability / reporting frequency (device Ping).</summary>
        Task<PingResponse> PingAsync(Guid clientId, Guid deviceId, CancellationToken ct);
        /// <summary>Goods in stock at FDMS for this device's store (getStockList).</summary>
        Task<StockListResponse> GetStockAsync(Guid clientId, Guid deviceId, string? hsCode, string? goodName, int offset, int limit, CancellationToken ct);
        /// <summary>The FDMS signing certificate chain, used to verify FDMS signatures (getServerCertificate).</summary>
        Task<ServerCertificateResponse> GetServerCertificateAsync(Guid clientId, Guid deviceId, string? thumbprint, CancellationToken ct);
    }

    public interface IOfflineFileService
    {
        Task<IReadOnlyList<OfflineFileDto>> ListAsync(Guid clientId, Guid deviceId, CancellationToken ct);
        /// <summary>Files up what has been issued so far, then uploads and checks status immediately.</summary>
        Task<IReadOnlyList<OfflineFileDto>> SyncAsync(Guid clientId, Guid deviceId, CancellationToken ct);
    }

    public interface IFiscalDayService
    {
        Task<FiscalDayDto> GetStatusAsync(Guid clientId, Guid deviceId, CancellationToken ct);
        Task<FiscalDayDto> OpenAsync(Guid clientId, Guid deviceId, CancellationToken ct);
        Task<FiscalDayDto> CloseAsync(Guid clientId, Guid deviceId, CancellationToken ct);
    }

    public interface IReceiptService
    {
        Task<ReceiptDto> SubmitAsync(Guid clientId, Guid deviceId, SubmitReceiptRequest req, CancellationToken ct);
        Task<ReceiptDto> GetAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        Task<ReceiptDetailDto> GetDetailAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        Task<CorrectionResult> CorrectAsync(Guid clientId, Guid receiptId, CorrectReceiptRequest req, CancellationToken ct);
        /// <summary>Issues a debit note against an accepted invoice. Repeating the same note returns the one already issued.</summary>
        Task<ReceiptDto> IssueDebitNoteAsync(Guid clientId, Guid receiptId, DebitNoteRequest req, CancellationToken ct);
        Task<ReceiptDto> RetryAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        /// <summary>Ends the hold now: the receipt is numbered, signed and sent.</summary>
        Task<ReceiptDto> ReleaseAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        /// <summary>Voids a receipt that is still held. Nothing was sent to ZIMRA and no number was used.</summary>
        Task<ReceiptDto> CancelAsync(Guid clientId, Guid receiptId, string? reason, CancellationToken ct);
        /// <summary>Issues held receipts whose grace period is over. Returns how many were processed.</summary>
        Task<int> ReleaseDueHeldAsync(CancellationToken ct);
        Task<byte[]> GetQrPngAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        /// <summary>Calculates taxes/totals exactly as a real submission would, without consuming numbers or contacting FDMS.</summary>
        Task<Import.CalculatedReceiptView> PreviewAsync(Guid clientId, Guid deviceId, SubmitReceiptRequest req, CancellationToken ct);
        Task<Import.PagedResult<ReceiptDto>> ListAsync(Guid clientId, Import.ReceiptListFilter filter, CancellationToken ct);
        /// <summary>Printable receipt. <paramref name="a4"/> false = 80 mm thermal roll.</summary>
        Task<byte[]> GetPdfAsync(Guid clientId, Guid receiptId, bool a4, CancellationToken ct);
        /// <summary>The invoice as a Word document laid out like the PDF.</summary>
        Task<byte[]> GetDocxAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        /// <summary>The invoice as structured data for other systems (every printed value, lines, taxes, totals, fiscal identifiers, and the layout).</summary>
        Task<InvoiceDocumentDto> GetDocumentAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        /// <summary>Re-sends receipts left Pending by FDMS outages, oldest first. Returns how many were delivered.</summary>
        Task<int> RetryPendingAsync(CancellationToken ct);
    }

    public interface IWebhookService
    {
        /// <summary>Sets the URL and (re)generates the signing secret, returned once.</summary>
        Task<WebhookConfigDto> SetAsync(Guid clientId, string url, CancellationToken ct);
        Task<WebhookConfigDto> GetAsync(Guid clientId, CancellationToken ct);
        Task ClearAsync(Guid clientId, CancellationToken ct);
        Task<IReadOnlyList<WebhookDeliveryDto>> ListAsync(Guid clientId, int take, CancellationToken ct);
    }
}
