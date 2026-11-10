using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Import;
using MediatR;

namespace Fdms.Application.Features
{
    // ---- devices ---------------------------------------------------------------------------------------------
    public record VerifyDeviceCommand(DeviceIdentityRequest Body) : IRequest<VerifyTaxpayerResponse>;
    public record RegisterDeviceCommand(Guid ClientId, DeviceIdentityRequest Body) : IRequest<DeviceDto>;
    public record ListDevicesQuery(Guid ClientId) : IRequest<IReadOnlyList<DeviceDto>>;
    public record GetDeviceQuery(Guid ClientId, Guid DeviceId) : IRequest<DeviceDto>;
    public record RefreshDeviceConfigCommand(Guid ClientId, Guid DeviceId) : IRequest<DeviceDto>;
    public record RenewCertificateCommand(Guid ClientId, Guid DeviceId) : IRequest<DeviceDto>;

    // ---- fiscal day ------------------------------------------------------------------------------------------
    public record GetFiscalDayStatusQuery(Guid ClientId, Guid DeviceId) : IRequest<FiscalDayDto>;
    public record OpenFiscalDayCommand(Guid ClientId, Guid DeviceId) : IRequest<FiscalDayDto>;
    public record CloseFiscalDayCommand(Guid ClientId, Guid DeviceId) : IRequest<FiscalDayDto>;

    // ---- receipts --------------------------------------------------------------------------------------------
    public record SubmitReceiptCommand(Guid ClientId, Guid DeviceId, SubmitReceiptRequest Body) : IRequest<ReceiptDto>;
    public record GetReceiptQuery(Guid ClientId, Guid ReceiptId) : IRequest<ReceiptDto>;
    public record GetReceiptDetailQuery(Guid ClientId, Guid ReceiptId) : IRequest<ReceiptDetailDto>;
    public record CorrectReceiptCommand(Guid ClientId, Guid ReceiptId, CorrectReceiptRequest Body) : IRequest<CorrectionResult>;
    public record IssueDebitNoteCommand(Guid ClientId, Guid ReceiptId, DebitNoteRequest Body) : IRequest<ReceiptDto>;
    public record RetryReceiptCommand(Guid ClientId, Guid ReceiptId) : IRequest<ReceiptDto>;
    public record GetReceiptQrQuery(Guid ClientId, Guid ReceiptId) : IRequest<byte[]>;

    public record PreviewReceiptCommand(Guid ClientId, Guid DeviceId, SubmitReceiptRequest Body) : IRequest<CalculatedReceiptView>;
    public record ListReceiptsQuery(Guid ClientId, ReceiptListFilter Filter) : IRequest<PagedResult<ReceiptDto>>;
    public record GetReceiptPdfQuery(Guid ClientId, Guid ReceiptId, bool A4) : IRequest<byte[]>;

    public record ReleaseReceiptCommand(Guid ClientId, Guid ReceiptId) : IRequest<ReceiptDto>;
    public record CancelReceiptCommand(Guid ClientId, Guid ReceiptId, string? Reason) : IRequest<ReceiptDto>;

    // ---- device extras / offline -----------------------------------------------------------------------------
    
    
    public record PingDeviceQuery(Guid ClientId, Guid DeviceId) : IRequest<PingResponse>;
    public record GetStockQuery(Guid ClientId, Guid DeviceId, string? HsCode, string? GoodName, int Offset, int Limit) : IRequest<StockListResponse>;
    public record GetServerCertificateQuery(Guid ClientId, Guid DeviceId, string? Thumbprint) : IRequest<ServerCertificateResponse>;
    public record ListOfflineFilesQuery(Guid ClientId, Guid DeviceId) : IRequest<IReadOnlyList<OfflineFileDto>>;
    public record SyncOfflineFilesCommand(Guid ClientId, Guid DeviceId) : IRequest<IReadOnlyList<OfflineFileDto>>;

    
    
    // ---- import ----------------------------------------------------------------------------------------------
    public enum ImportKind { Excel, Pdf, Word }
    public record ImportInvoicesCommand(Guid ClientId, Guid DeviceId, ImportKind Kind, byte[] File, ImportOptions Options) : IRequest<ImportResultDto>;

    // ---- webhooks --------------------------------------------------------------------------------------------
    
    public record SetWebhookCommand(Guid ClientId, string Url) : IRequest<WebhookConfigDto>;
    public record GetWebhookQuery(Guid ClientId) : IRequest<WebhookConfigDto>;
    public record ClearWebhookCommand(Guid ClientId) : IRequest<Unit>;
    public record ListWebhookDeliveriesQuery(Guid ClientId, int Take) : IRequest<IReadOnlyList<WebhookDeliveryDto>>;


    /// <summary>One thin handler per use case, delegating to the service that owns the rules (same shape as dara/fiscal).</summary>
    public sealed class DeviceHandlers :
        IRequestHandler<VerifyDeviceCommand, VerifyTaxpayerResponse>,
        IRequestHandler<RegisterDeviceCommand, DeviceDto>,
        IRequestHandler<ListDevicesQuery, IReadOnlyList<DeviceDto>>,
        IRequestHandler<GetDeviceQuery, DeviceDto>,
        IRequestHandler<RefreshDeviceConfigCommand, DeviceDto>,
        IRequestHandler<RenewCertificateCommand, DeviceDto>,
        IRequestHandler<PingDeviceQuery, PingResponse>,
        IRequestHandler<GetStockQuery, StockListResponse>,
        IRequestHandler<GetServerCertificateQuery, ServerCertificateResponse>
    {
        private readonly IDeviceService _s;
        public DeviceHandlers(IDeviceService s) => _s = s;

        public Task<VerifyTaxpayerResponse> Handle(VerifyDeviceCommand r, CancellationToken ct) => _s.VerifyAsync(r.Body, ct);
        public Task<DeviceDto> Handle(RegisterDeviceCommand r, CancellationToken ct) => _s.RegisterAsync(r.ClientId, r.Body, ct);
        public Task<IReadOnlyList<DeviceDto>> Handle(ListDevicesQuery r, CancellationToken ct) => _s.ListAsync(r.ClientId, ct);
        public Task<DeviceDto> Handle(GetDeviceQuery r, CancellationToken ct) => _s.GetAsync(r.ClientId, r.DeviceId, ct);
        public Task<DeviceDto> Handle(RefreshDeviceConfigCommand r, CancellationToken ct) => _s.RefreshConfigAsync(r.ClientId, r.DeviceId, ct);
        public Task<DeviceDto> Handle(RenewCertificateCommand r, CancellationToken ct) => _s.RenewCertificateAsync(r.ClientId, r.DeviceId, ct);
        public Task<PingResponse> Handle(PingDeviceQuery r, CancellationToken ct) => _s.PingAsync(r.ClientId, r.DeviceId, ct);
        public Task<StockListResponse> Handle(GetStockQuery r, CancellationToken ct) => _s.GetStockAsync(r.ClientId, r.DeviceId, r.HsCode, r.GoodName, r.Offset, r.Limit, ct);
        public Task<ServerCertificateResponse> Handle(GetServerCertificateQuery r, CancellationToken ct) => _s.GetServerCertificateAsync(r.ClientId, r.DeviceId, r.Thumbprint, ct);
    }

    public sealed class FiscalDayHandlers :
        IRequestHandler<GetFiscalDayStatusQuery, FiscalDayDto>,
        IRequestHandler<OpenFiscalDayCommand, FiscalDayDto>,
        IRequestHandler<CloseFiscalDayCommand, FiscalDayDto>
    {
        private readonly IFiscalDayService _s;
        public FiscalDayHandlers(IFiscalDayService s) => _s = s;

        public Task<FiscalDayDto> Handle(GetFiscalDayStatusQuery r, CancellationToken ct) => _s.GetStatusAsync(r.ClientId, r.DeviceId, ct);
        public Task<FiscalDayDto> Handle(OpenFiscalDayCommand r, CancellationToken ct) => _s.OpenAsync(r.ClientId, r.DeviceId, ct);
        public Task<FiscalDayDto> Handle(CloseFiscalDayCommand r, CancellationToken ct) => _s.CloseAsync(r.ClientId, r.DeviceId, ct);
    }

    public sealed class ReceiptHandlers :
        IRequestHandler<SubmitReceiptCommand, ReceiptDto>,
        IRequestHandler<GetReceiptQuery, ReceiptDto>,
        IRequestHandler<GetReceiptDetailQuery, ReceiptDetailDto>,
        IRequestHandler<CorrectReceiptCommand, CorrectionResult>,
        IRequestHandler<IssueDebitNoteCommand, ReceiptDto>,
        IRequestHandler<RetryReceiptCommand, ReceiptDto>,
        IRequestHandler<GetReceiptQrQuery, byte[]>,
        IRequestHandler<PreviewReceiptCommand, CalculatedReceiptView>,
        IRequestHandler<ListReceiptsQuery, PagedResult<ReceiptDto>>,
        IRequestHandler<GetReceiptPdfQuery, byte[]>,
        IRequestHandler<ReleaseReceiptCommand, ReceiptDto>,
        IRequestHandler<CancelReceiptCommand, ReceiptDto>
    {
        private readonly IReceiptService _s;
        public ReceiptHandlers(IReceiptService s) => _s = s;

        public Task<ReceiptDto> Handle(SubmitReceiptCommand r, CancellationToken ct) => _s.SubmitAsync(r.ClientId, r.DeviceId, r.Body, ct);
        public Task<ReceiptDto> Handle(GetReceiptQuery r, CancellationToken ct) => _s.GetAsync(r.ClientId, r.ReceiptId, ct);
        public Task<ReceiptDetailDto> Handle(GetReceiptDetailQuery r, CancellationToken ct) => _s.GetDetailAsync(r.ClientId, r.ReceiptId, ct);
        public Task<CorrectionResult> Handle(CorrectReceiptCommand r, CancellationToken ct) => _s.CorrectAsync(r.ClientId, r.ReceiptId, r.Body, ct);
        public Task<ReceiptDto> Handle(IssueDebitNoteCommand r, CancellationToken ct) => _s.IssueDebitNoteAsync(r.ClientId, r.ReceiptId, r.Body, ct);
        public Task<ReceiptDto> Handle(RetryReceiptCommand r, CancellationToken ct) => _s.RetryAsync(r.ClientId, r.ReceiptId, ct);
        public Task<byte[]> Handle(GetReceiptQrQuery r, CancellationToken ct) => _s.GetQrPngAsync(r.ClientId, r.ReceiptId, ct);
        public Task<CalculatedReceiptView> Handle(PreviewReceiptCommand r, CancellationToken ct) => _s.PreviewAsync(r.ClientId, r.DeviceId, r.Body, ct);
        public Task<PagedResult<ReceiptDto>> Handle(ListReceiptsQuery r, CancellationToken ct) => _s.ListAsync(r.ClientId, r.Filter, ct);
        public Task<byte[]> Handle(GetReceiptPdfQuery r, CancellationToken ct) => _s.GetPdfAsync(r.ClientId, r.ReceiptId, r.A4, ct);
        public Task<ReceiptDto> Handle(ReleaseReceiptCommand r, CancellationToken ct) => _s.ReleaseAsync(r.ClientId, r.ReceiptId, ct);
        public Task<ReceiptDto> Handle(CancelReceiptCommand r, CancellationToken ct) => _s.CancelAsync(r.ClientId, r.ReceiptId, r.Reason, ct);
    }

    public sealed class OfflineHandlers :
        IRequestHandler<ListOfflineFilesQuery, IReadOnlyList<OfflineFileDto>>,
        IRequestHandler<SyncOfflineFilesCommand, IReadOnlyList<OfflineFileDto>>
    {
        private readonly IOfflineFileService _s;
        public OfflineHandlers(IOfflineFileService s) => _s = s;
        public Task<IReadOnlyList<OfflineFileDto>> Handle(ListOfflineFilesQuery r, CancellationToken ct) => _s.ListAsync(r.ClientId, r.DeviceId, ct);
        public Task<IReadOnlyList<OfflineFileDto>> Handle(SyncOfflineFilesCommand r, CancellationToken ct) => _s.SyncAsync(r.ClientId, r.DeviceId, ct);
    }

    public sealed class ImportHandlers : IRequestHandler<ImportInvoicesCommand, ImportResultDto>
    {
        private readonly IInvoiceImportService _s;
        public ImportHandlers(IInvoiceImportService s) => _s = s;

        public Task<ImportResultDto> Handle(ImportInvoicesCommand r, CancellationToken ct)
        {
            var stream = new MemoryStream(r.File);
            return r.Kind switch
            {
                ImportKind.Excel => _s.ImportExcelAsync(r.ClientId, r.DeviceId, stream, r.Options, ct),
                ImportKind.Word => _s.ImportWordAsync(r.ClientId, r.DeviceId, stream, r.Options, ct),
                _ => _s.ImportPdfAsync(r.ClientId, r.DeviceId, stream, r.Options, ct),
            };
        }
    }

    public sealed class WebhookHandlers :
        IRequestHandler<SetWebhookCommand, WebhookConfigDto>,
        IRequestHandler<GetWebhookQuery, WebhookConfigDto>,
        IRequestHandler<ClearWebhookCommand, Unit>,
        IRequestHandler<ListWebhookDeliveriesQuery, IReadOnlyList<WebhookDeliveryDto>>
    {
        private readonly IWebhookService _s;
        public WebhookHandlers(IWebhookService s) => _s = s;

        public Task<WebhookConfigDto> Handle(SetWebhookCommand r, CancellationToken ct) => _s.SetAsync(r.ClientId, r.Url, ct);
        public Task<WebhookConfigDto> Handle(GetWebhookQuery r, CancellationToken ct) => _s.GetAsync(r.ClientId, ct);
        public async Task<Unit> Handle(ClearWebhookCommand r, CancellationToken ct) { await _s.ClearAsync(r.ClientId, ct); return Unit.Value; }
        public Task<IReadOnlyList<WebhookDeliveryDto>> Handle(ListWebhookDeliveriesQuery r, CancellationToken ct) => _s.ListAsync(r.ClientId, r.Take, ct);
    }
}
