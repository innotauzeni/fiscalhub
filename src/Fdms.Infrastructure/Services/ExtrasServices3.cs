using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Fdms.Infrastructure.Services
{
    /// <summary>Shared by quotes and recurring invoices: the same line shape, the same checks, the same total.</summary>
    internal static class LineTools
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly Regex Hs = new(@"^(\d{4}|\d{6}|\d{8})$", RegexOptions.Compiled);

        public static IReadOnlyList<QuoteLineDto> Read(string json)
        {
            try { return JsonSerializer.Deserialize<List<QuoteLineDto>>(json, Json) ?? new(); } catch (JsonException) { return new List<QuoteLineDto>(); }
        }
        public static string Write(IReadOnlyList<QuoteLineDto> lines) => JsonSerializer.Serialize(lines, Json);

        public static decimal Total(IReadOnlyList<QuoteLineDto> lines, bool inclusive) =>
            Math.Round(lines.Sum(l =>
            {
                var gross = l.Quantity * l.UnitPrice;
                var rate = l.IsExempt || l.TaxPercent == null ? 0m : l.TaxPercent.Value / 100m;
                return inclusive ? gross : gross * (1 + rate);
            }), 2);

        public static IReadOnlyList<QuoteLineDto> Clean(IReadOnlyList<QuoteLineDto>? lines)
        {
            if (lines == null || lines.Count == 0) throw GatewayException.BadRequest("LINES_REQUIRED", "Add at least one line.");
            if (lines.Count > 100) throw GatewayException.BadRequest("TOO_MANY_LINES", "At most 100 lines.");
            var clean = new List<QuoteLineDto>();
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i]; var name = l.Name?.Trim() ?? "";
                if (name.Length == 0 || name.Length > 200) throw GatewayException.BadRequest("LINE_NAME_INVALID", $"Line {i + 1}: enter a name (up to 200 characters).");
                if (l.Quantity <= 0 || l.UnitPrice < 0) throw GatewayException.BadRequest("LINE_AMOUNT_INVALID", $"Line {i + 1}: the quantity must be above zero and the price cannot be negative.");
                var hs = string.IsNullOrWhiteSpace(l.HsCode) ? null : l.HsCode.Trim();
                if (hs != null && !Hs.IsMatch(hs)) throw GatewayException.BadRequest("HS_INVALID", $"Line {i + 1}: the HS code must have 4, 6 or 8 digits.");
                if (l.TaxPercent is < 0 or > 100) throw GatewayException.BadRequest("TAX_INVALID", $"Line {i + 1}: the VAT rate must be between 0 and 100.");
                clean.Add(new QuoteLineDto(name, hs, l.Quantity, l.UnitPrice, l.IsExempt ? null : l.TaxPercent, l.IsExempt));
            }
            return clean;
        }

        public static ReceiptLineRequest ToLine(QuoteLineDto l) => new(l.Name, l.HsCode, l.Quantity, l.UnitPrice, TaxPercent: l.IsExempt ? null : l.TaxPercent, IsExempt: l.IsExempt);

        public static BuyerRequest? Buyer(string name, string? tin, string? vat, string? email, string? phone) =>
            tin is { Length: 10 } && tin.All(char.IsDigit) ? new BuyerRequest(name, null, tin, string.IsNullOrWhiteSpace(vat) ? null : vat, string.IsNullOrWhiteSpace(email) ? null : email, string.IsNullOrWhiteSpace(phone) ? null : phone) : null;
    }

    // =================================================================================================================
    public sealed class QuoteService : IQuoteService
    {
        private static readonly string[] Editable = { "Draft", "Sent", "Accepted", "Declined" };
        private readonly FdmsDbContext _db;
        private readonly IReceiptService _receipts;
        private readonly IEmailSender _email;
        private readonly InvoiceTemplateService _templates;
        private readonly IEmailOutbox _outbox;
        public QuoteService(FdmsDbContext db, IReceiptService receipts, IEmailSender email, InvoiceTemplateService templates, IEmailOutbox outbox) { _db = db; _receipts = receipts; _email = email; _templates = templates; _outbox = outbox; }

        private static QuoteDto ToDto(Quote q)
        {
            var lines = LineTools.Read(q.LinesJson);
            return new QuoteDto(q.Id, q.Number, q.CustomerName, q.CustomerTin, q.CustomerVat, q.CustomerEmail, q.CustomerPhone, q.Currency, q.TaxInclusive, q.ValidUntil, q.Notes, q.Status, lines,
                LineTools.Total(lines, q.TaxInclusive), q.InvoiceReceiptId, q.CreatedAt);
        }

        private async Task<Quote> LoadAsync(Guid clientId, Guid id, CancellationToken ct) =>
            await _db.Quotes.FirstOrDefaultAsync(q => q.Id == id && q.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Quote");

        public async Task<IReadOnlyList<QuoteDto>> ListAsync(Guid clientId, string? status, string? search, CancellationToken ct)
        {
            var q = _db.Quotes.AsNoTracking().Where(x => x.ClientId == clientId);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
            if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); q = q.Where(x => x.Number.Contains(s) || x.CustomerName.Contains(s)); }
            return (await q.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(ct)).Select(ToDto).ToList();
        }

        public async Task<QuoteDto> GetAsync(Guid clientId, Guid id, CancellationToken ct) => ToDto(await _db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id && q.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Quote"));

        public async Task<QuoteDto> SaveAsync(Guid clientId, SaveQuoteRequest r, CancellationToken ct)
        {
            var name = r.CustomerName?.Trim() ?? "";
            if (name.Length < 2 || name.Length > 200) throw GatewayException.BadRequest("CUSTOMER_REQUIRED", "Enter the customer's name.");
            if (!string.IsNullOrWhiteSpace(r.CustomerTin) && !(r.CustomerTin.Length == 10 && r.CustomerTin.All(char.IsDigit))) throw GatewayException.BadRequest("TIN_INVALID", "A TIN has 10 digits.");
            if (!string.IsNullOrWhiteSpace(r.CustomerEmail) && !Regex.IsMatch(r.CustomerEmail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter a valid email address.");
            var currency = (r.Currency ?? "").Trim().ToUpperInvariant();
            if (currency.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "The currency must be a 3-letter code.");
            var lines = LineTools.Clean(r.Lines);

            Quote q;
            if (r.Id is { } id)
            {
                q = await LoadAsync(clientId, id, ct);
                if (q.Status == "Invoiced") throw GatewayException.Conflict("QUOTE_INVOICED", "This quote has already become an invoice and cannot be changed.");
            }
            else
            {
                q = new Quote { ClientId = clientId };
                _db.Quotes.Add(q);
            }
            var number = r.Number?.Trim();
            if (string.IsNullOrEmpty(number))
            {
                if (r.Id != null) number = q.Number;
                else
                {
                    var n = (await _db.Quotes.Where(x => x.ClientId == clientId).CountAsync(ct)) + 1;
                    do number = $"Q-{n++:0000}"; while (await _db.Quotes.AnyAsync(x => x.ClientId == clientId && x.Number == number, ct));
                }
            }
            if (number!.Length > 50) throw GatewayException.BadRequest("NUMBER_TOO_LONG", "The quote number may have at most 50 characters.");
            if (await _db.Quotes.AnyAsync(x => x.ClientId == clientId && x.Number == number && x.Id != q.Id, ct)) throw GatewayException.Conflict("NUMBER_USED", "That quote number is already used.");
            q.Number = number; q.CustomerName = name; q.CustomerTin = string.IsNullOrWhiteSpace(r.CustomerTin) ? null : r.CustomerTin.Trim(); q.CustomerVat = string.IsNullOrWhiteSpace(r.CustomerVat) ? null : r.CustomerVat.Trim();
            q.CustomerEmail = string.IsNullOrWhiteSpace(r.CustomerEmail) ? null : r.CustomerEmail.Trim(); q.CustomerPhone = string.IsNullOrWhiteSpace(r.CustomerPhone) ? null : r.CustomerPhone.Trim();
            q.Currency = currency; q.TaxInclusive = r.TaxInclusive; q.ValidUntil = r.ValidUntil?.Date; q.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
            q.LinesJson = LineTools.Write(lines); q.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return ToDto(q);
        }

        public async Task<QuoteDto> SetStatusAsync(Guid clientId, Guid id, string status, CancellationToken ct)
        {
            if (!Editable.Contains(status)) throw GatewayException.BadRequest("STATUS_INVALID", "Status must be Draft, Sent, Accepted or Declined.");
            var q = await LoadAsync(clientId, id, ct);
            if (q.Status == "Invoiced") throw GatewayException.Conflict("QUOTE_INVOICED", "This quote has already become an invoice.");
            q.Status = status; q.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return ToDto(q);
        }

        public async Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var q = await LoadAsync(clientId, id, ct);
            if (q.Status == "Invoiced") throw GatewayException.Conflict("QUOTE_INVOICED", "A quote that became an invoice is kept as the record of it.");
            _db.Quotes.Remove(q); await _db.SaveChangesAsync(ct);
        }

        public async Task<ReceiptDto> ConvertAsync(Guid clientId, Guid id, ConvertQuoteRequest req, CancellationToken ct)
        {
            var q = await LoadAsync(clientId, id, ct);
            if (q.Status == "Declined") throw GatewayException.Conflict("QUOTE_DECLINED", "The customer declined this quote. Reopen it (set it to Accepted) before invoicing.");
            var lines = LineTools.Read(q.LinesJson);
            var extRef = $"quote-{q.Id:N}";   // converting twice returns the invoice already issued, never a second one
            var no = string.IsNullOrWhiteSpace(req.InvoiceNo) ? "INV-" + Regex.Replace(q.Number, @"^[A-Za-z]+[-_/ ]*", "") : req.InvoiceNo.Trim();
            if (no.Length > 50) throw GatewayException.BadRequest("INVOICE_NO_TOO_LONG", "The invoice number may have at most 50 characters.");
            var method = Enum.TryParse<MoneyType>(req.PaymentMethod, true, out var m) ? m : MoneyType.Cash;

            var draft = new SubmitReceiptRequest(extRef, no, ReceiptType.FiscalInvoice, q.Currency, q.TaxInclusive, lines.Select(LineTools.ToLine).ToList(), null,
                LineTools.Buyer(q.CustomerName, q.CustomerTin, q.CustomerVat, q.CustomerEmail, q.CustomerPhone), string.IsNullOrWhiteSpace(q.Notes) ? $"Quotation {q.Number}" : q.Notes, AutoOpenDay: true);
            if (method != MoneyType.Cash)
            {
                var view = await _receipts.PreviewAsync(clientId, req.DeviceId, draft, ct);
                draft = draft with { Payments = new[] { new ReceiptPaymentRequest(method, view.Total) } };
            }
            var receipt = await _receipts.SubmitAsync(clientId, req.DeviceId, draft, ct);
            if (receipt.Status is "Submitted" or "Pending" or "OfflineIssued")
            {
                q.Status = "Invoiced"; q.InvoiceReceiptId = receipt.Id; q.UpdatedAt = DateTime.UtcNow;
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "quote.invoice", Entity = "Quote", EntityId = q.Id.ToString(), Detail = $"{q.Number} invoiced as {receipt.InvoiceNo}" });
                await _db.SaveChangesAsync(ct);
            }
            return receipt;
        }

        public async Task<byte[]> PdfAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var q = await LoadAsync(clientId, id, ct);
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstAsync(ct);
            var dev = await _db.Devices.AsNoTracking().Where(d => d.ClientId == clientId && d.TaxPayerName != null).OrderBy(d => d.ZimraDeviceId).FirstOrDefaultAsync(ct);
            var (settings, logo) = await _templates.ResolveAsync(clientId, null, ct);
            return QuotePdf.Render(ToDto(q), dev?.TaxPayerName ?? company, dev?.TaxPayerTin, dev?.VatNumber, settings.Accent ?? "#2563eb", logo);
        }

        public async Task<SendResultDto> EmailAsync(Guid clientId, Guid id, string? to, string? message, CancellationToken ct)
        {
            var q = await LoadAsync(clientId, id, ct);
            var addr = string.IsNullOrWhiteSpace(to) ? q.CustomerEmail : to.Trim();
            if (string.IsNullOrWhiteSpace(addr) || !Regex.IsMatch(addr, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw GatewayException.BadRequest("EMAIL_REQUIRED", "Enter the customer's email address.");
            if (!_email.Configured) return new SendResultDto(false, false, addr, "Email is not set up on this server. Download the PDF and send it yourself.");
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstAsync(ct);
            var note = string.IsNullOrWhiteSpace(message) ? "" : $"<p>{System.Net.WebUtility.HtmlEncode(message.Trim())}</p>";
            var subject = $"Quotation {q.Number} from {company}";
            var html = $"<p>Hello,</p>{note}<p>Please find quotation <b>{System.Net.WebUtility.HtmlEncode(q.Number)}</b> from <b>{System.Net.WebUtility.HtmlEncode(company)}</b> attached.</p>";
            var files = new[] { new EmailAttachment($"{q.Number}.pdf", await PdfAsync(clientId, id, ct), "application/pdf") };
            var result = await _email.DeliverAsync(addr, subject, html, files, ct);
            if (!result.Ok)
            {
                if (!result.Temporary) return new SendResultDto(false, true, addr, result.Error + " Download the PDF and send it yourself.");
                await _outbox.EnqueueAsync(clientId, addr, subject, html, files, result.Error, ct);
                return new SendResultDto(false, true, addr, "The mail server is busy right now, so the email is queued and will be tried again automatically.", Queued: true);
            }
            if (q.Status == "Draft") { q.Status = "Sent"; q.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); }
            return new SendResultDto(true, true, addr, "Sent.");
        }
    }

    /// <summary>A plain, professional quotation (not a fiscal document: it carries no ZIMRA number until it is invoiced).</summary>
    internal static class QuotePdf
    {
        static QuotePdf() => QuestPDF.Settings.License = LicenseType.Community;

        public static byte[] Render(QuoteDto q, string company, string? tin, string? vat, string accent, byte[]? logo)
        {
            var c = CultureInfo.InvariantCulture;
            var showLogo = logo is { Length: > 0 } && ReceiptPdfRenderer.CanDecode(logo);
            string M(decimal v) => v.ToString("N2", c);
            return Document.Create(doc => doc.Page(page =>
            {
                page.Size(PageSizes.A4); page.Margin(40); page.DefaultTextStyle(t => t.FontSize(10));
                page.Content().Column(col =>
                {
                    col.Spacing(12);
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c1 =>
                        {
                            if (showLogo) c1.Item().MaxWidth(170).MaxHeight(58).Image(logo!).FitArea();
                            c1.Item().Text(company).Bold().FontSize(15).FontColor(accent);
                            if (!string.IsNullOrEmpty(tin)) c1.Item().Text("TIN: " + tin);
                            if (!string.IsNullOrEmpty(vat)) c1.Item().Text("VAT No: " + vat);
                        });
                        row.ConstantItem(200).Column(c2 =>
                        {
                            c2.Item().AlignRight().Text("QUOTATION").Bold().FontSize(24).FontColor(accent);
                            c2.Item().AlignRight().Text(q.Number).Bold().FontSize(12);
                            c2.Item().AlignRight().Text("Date " + q.CreatedAt.ToString("d MMM yyyy", c));
                            if (q.ValidUntil != null) c2.Item().AlignRight().Text("Valid until " + q.ValidUntil.Value.ToString("d MMM yyyy", c));
                        });
                    });
                    col.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(b =>
                    {
                        b.Item().Text("Prepared for").FontSize(8).FontColor(Colors.Grey.Darken1);
                        b.Item().Text(q.CustomerName).Bold().FontSize(12);
                        if (!string.IsNullOrEmpty(q.CustomerTin)) b.Item().Text("TIN: " + q.CustomerTin + (string.IsNullOrEmpty(q.CustomerVat) ? "" : "   VAT No: " + q.CustomerVat));
                        if (!string.IsNullOrEmpty(q.CustomerEmail)) b.Item().Text(q.CustomerEmail);
                    });
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(24); cd.RelativeColumn(4); cd.ConstantColumn(50); cd.ConstantColumn(70); cd.ConstantColumn(50); cd.ConstantColumn(80); });
                        t.Header(h =>
                        {
                            foreach (var (txt, right) in new[] { ("#", false), ("Description", false), ("Qty", true), ("Unit price", true), ("VAT", true), ("Amount", true) })
                            { var cell = h.Cell().Background(accent).Padding(4); (right ? cell.AlignRight() : cell).Text(txt).Bold().FontColor(Colors.White); }
                        });
                        var i = 0;
                        foreach (var l in q.Lines)
                        {
                            i++;
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(i.ToString());
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(l.Name + (string.IsNullOrEmpty(l.HsCode) ? "" : "  [" + l.HsCode + "]"));
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(l.Quantity.ToString("0.##", c));
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(M(l.UnitPrice));
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(l.IsExempt ? "Exempt" : l.TaxPercent is { } p ? p.ToString("0.##", c) + "%" : "");
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(M(Math.Round(l.Quantity * l.UnitPrice * (q.TaxInclusive || l.IsExempt || l.TaxPercent == null ? 1 : 1 + l.TaxPercent.Value / 100m), 2)));
                        }
                    });
                    col.Item().AlignRight().Text($"Total {M(q.Total)} {q.Currency}").Bold().FontSize(13);
                    col.Item().AlignRight().Text(q.TaxInclusive ? "Prices include VAT" : "VAT is added to the prices shown").FontSize(8).FontColor(Colors.Grey.Darken1);
                    if (!string.IsNullOrWhiteSpace(q.Notes)) col.Item().Text(q.Notes!).FontSize(9);
                    col.Item().PaddingTop(10).Text("This is a quotation, not a tax invoice. A fiscal tax invoice is issued when the quotation is accepted.").FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            })).GeneratePdf();
        }
    }

    // =================================================================================================================
    public sealed class RecurringService : IRecurringService
    {
        private static readonly string[] Frequencies = { "Weekly", "Monthly", "Quarterly", "Yearly" };
        private readonly FdmsDbContext _db;
        private readonly IReceiptService _receipts;
        private readonly IInvoiceShareService _share;
        private readonly INotificationService _notify;
        private readonly ILogger<RecurringService> _log;
        public RecurringService(FdmsDbContext db, IReceiptService receipts, IInvoiceShareService share, INotificationService notify, ILogger<RecurringService> log) { _db = db; _receipts = receipts; _share = share; _notify = notify; _log = log; }

        public static DateTime Advance(DateTime d, string frequency) => frequency switch
        {
            "Weekly" => d.AddDays(7), "Quarterly" => d.AddMonths(3), "Yearly" => d.AddYears(1), _ => d.AddMonths(1),
        };

        private async Task<RecurringDto> ToDtoAsync(RecurringInvoice r, CancellationToken ct)
        {
            var lines = LineTools.Read(r.LinesJson);
            var zimra = await _db.Devices.AsNoTracking().Where(d => d.Id == r.DeviceId).Select(d => d.ZimraDeviceId).FirstOrDefaultAsync(ct);
            var runs = await _db.RecurringRuns.AsNoTracking().Where(x => x.RecurringInvoiceId == r.Id).OrderByDescending(x => x.RunDate).Take(5).ToListAsync(ct);
            return new RecurringDto(r.Id, r.DeviceId, zimra, r.Name, r.CustomerName, r.CustomerTin, r.CustomerVat, r.CustomerEmail, r.CustomerPhone, r.Currency, r.TaxInclusive, r.PaymentMethod, r.Notes, lines,
                r.Frequency, r.NextRunDate, r.EndDate, r.IsActive, r.Prefix, r.NextNumber, r.EmailCustomer, LineTools.Total(lines, r.TaxInclusive),
                runs.Select(x => new RecurringRunDto(x.Id, x.RunDate, x.ReceiptId, x.InvoiceNo, x.Status, x.Error, x.CreatedAt)).ToList());
        }

        private async Task<RecurringInvoice> LoadAsync(Guid clientId, Guid id, CancellationToken ct) =>
            await _db.RecurringInvoices.FirstOrDefaultAsync(r => r.Id == id && r.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Recurring invoice");

        public async Task<IReadOnlyList<RecurringDto>> ListAsync(Guid clientId, CancellationToken ct)
        {
            var list = await _db.RecurringInvoices.AsNoTracking().Where(r => r.ClientId == clientId).OrderBy(r => r.NextRunDate).ToListAsync(ct);
            var result = new List<RecurringDto>();
            foreach (var r in list) result.Add(await ToDtoAsync(r, ct));
            return result;
        }

        public async Task<RecurringDto> SaveAsync(Guid clientId, SaveRecurringRequest req, CancellationToken ct)
        {
            var name = req.Name?.Trim() ?? "";
            if (name.Length < 2 || name.Length > 150) throw GatewayException.BadRequest("NAME_REQUIRED", "Give the schedule a name (for example “Office rent, Moyo Retailers”).");
            var customer = req.CustomerName?.Trim() ?? "";
            if (customer.Length < 2 || customer.Length > 200) throw GatewayException.BadRequest("CUSTOMER_REQUIRED", "Enter the customer's name.");
            if (!string.IsNullOrWhiteSpace(req.CustomerTin) && !(req.CustomerTin.Length == 10 && req.CustomerTin.All(char.IsDigit))) throw GatewayException.BadRequest("TIN_INVALID", "A TIN has 10 digits.");
            if (!string.IsNullOrWhiteSpace(req.CustomerEmail) && !Regex.IsMatch(req.CustomerEmail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter a valid email address.");
            if (req.EmailCustomer && string.IsNullOrWhiteSpace(req.CustomerEmail)) throw GatewayException.BadRequest("EMAIL_REQUIRED", "Enter the customer's email address to email each invoice.");
            if (!Frequencies.Contains(req.Frequency)) throw GatewayException.BadRequest("FREQUENCY_INVALID", "Choose Weekly, Monthly, Quarterly or Yearly.");
            var currency = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (currency.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "The currency must be a 3-letter code.");
            if (!Enum.TryParse<MoneyType>(req.PaymentMethod, true, out _)) throw GatewayException.BadRequest("PAYMENT_INVALID", "Choose how the invoice is paid.");
            var prefix = (req.Prefix ?? "REC-").Trim();
            if (!Regex.IsMatch(prefix, @"^[A-Za-z0-9\-_/]{0,20}$")) throw GatewayException.BadRequest("PREFIX_INVALID", "The number prefix may have up to 20 letters, digits, dashes or slashes.");
            if (req.EndDate != null && req.EndDate.Value.Date < req.NextRunDate.Date) throw GatewayException.BadRequest("END_BEFORE_START", "The end date is before the first invoice date.");
            var lines = LineTools.Clean(req.Lines);
            if (!await _db.Devices.AnyAsync(d => d.Id == req.DeviceId && d.ClientId == clientId && d.IsActive, ct)) throw GatewayException.NotFound("Device");

            RecurringInvoice r;
            if (req.Id is { } id) r = await LoadAsync(clientId, id, ct); else { r = new RecurringInvoice { ClientId = clientId }; _db.RecurringInvoices.Add(r); }
            r.DeviceId = req.DeviceId; r.Name = name; r.CustomerName = customer; r.CustomerTin = string.IsNullOrWhiteSpace(req.CustomerTin) ? null : req.CustomerTin.Trim();
            r.CustomerVat = string.IsNullOrWhiteSpace(req.CustomerVat) ? null : req.CustomerVat.Trim(); r.CustomerEmail = string.IsNullOrWhiteSpace(req.CustomerEmail) ? null : req.CustomerEmail.Trim();
            r.CustomerPhone = string.IsNullOrWhiteSpace(req.CustomerPhone) ? null : req.CustomerPhone.Trim(); r.Currency = currency; r.TaxInclusive = req.TaxInclusive;
            r.PaymentMethod = req.PaymentMethod; r.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(); r.LinesJson = LineTools.Write(lines); r.Frequency = req.Frequency;
            r.NextRunDate = req.NextRunDate.Date; r.EndDate = req.EndDate?.Date; r.Prefix = prefix; r.EmailCustomer = req.EmailCustomer;
            r.IsActive = true;
            await _db.SaveChangesAsync(ct);
            return await ToDtoAsync(r, ct);
        }

        public async Task<RecurringDto> SetActiveAsync(Guid clientId, Guid id, bool active, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, id, ct); r.IsActive = active; await _db.SaveChangesAsync(ct); return await ToDtoAsync(r, ct);
        }

        public async Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, id, ct);
            _db.RecurringRuns.RemoveRange(await _db.RecurringRuns.Where(x => x.RecurringInvoiceId == id).ToListAsync(ct));
            _db.RecurringInvoices.Remove(r); await _db.SaveChangesAsync(ct);   // the invoices already issued are fiscal records and stay
        }

        /// <summary>Issues the invoice for the schedule's next date. True when it was issued.</summary>
        private async Task<bool> RunOneAsync(RecurringInvoice r, CancellationToken ct)
        {
            var runDate = r.NextRunDate.Date;
            var run = await _db.RecurringRuns.FirstOrDefaultAsync(x => x.RecurringInvoiceId == r.Id && x.RunDate == runDate, ct);
            if (run == null) { run = new RecurringRun { RecurringInvoiceId = r.Id, ClientId = r.ClientId, RunDate = runDate }; _db.RecurringRuns.Add(run); }
            try
            {
                var no = $"{r.Prefix}{r.NextNumber:0000}";
                var lines = LineTools.Read(r.LinesJson);
                var draft = new SubmitReceiptRequest($"recurring-{r.Id:N}-{runDate:yyyyMMdd}", no, ReceiptType.FiscalInvoice, r.Currency, r.TaxInclusive, lines.Select(LineTools.ToLine).ToList(), null,
                    LineTools.Buyer(r.CustomerName, r.CustomerTin, r.CustomerVat, r.CustomerEmail, r.CustomerPhone), r.Notes, AutoOpenDay: true);
                var method = Enum.Parse<MoneyType>(r.PaymentMethod, true);
                if (method != MoneyType.Cash)
                {
                    var view = await _receipts.PreviewAsync(r.ClientId, r.DeviceId, draft, ct);
                    draft = draft with { Payments = new[] { new ReceiptPaymentRequest(method, view.Total) } };
                }
                var receipt = await _receipts.SubmitAsync(r.ClientId, r.DeviceId, draft, ct);
                if (receipt.Status == "Rejected") throw GatewayException.Unprocessable(receipt.ErrorCode ?? "RECEIPT_REJECTED", receipt.ErrorMessage ?? "ZIMRA rejected the invoice.");
                run.Status = "Ok"; run.ReceiptId = receipt.Id; run.InvoiceNo = receipt.InvoiceNo; run.Error = null;
                r.NextNumber++; r.NextRunDate = Advance(runDate, r.Frequency);
                if (r.EndDate != null && r.NextRunDate > r.EndDate) r.IsActive = false;
                _db.AuditLogs.Add(new AuditLog { ClientId = r.ClientId, Action = "recurring.issue", Entity = "RecurringInvoice", EntityId = r.Id.ToString(), Detail = $"{r.Name}: {receipt.InvoiceNo} for {runDate:yyyy-MM-dd}" });
                await _db.SaveChangesAsync(ct);
                if (r.EmailCustomer && !string.IsNullOrWhiteSpace(r.CustomerEmail))
                    try { await _share.EmailAsync(r.ClientId, receipt.Id, new SendInvoiceRequest(r.CustomerEmail, null), ct); } catch (Exception ex) { _log.LogWarning(ex, "Recurring invoice email failed"); }
                return true;
            }
            catch (Exception ex) when (ex is GatewayException or FdmsException)
            {
                run.Status = "Failed"; run.Error = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
                await _db.SaveChangesAsync(ct);
                await _notify.RaiseAsync(r.ClientId, "recurring", "bad", $"recurring:{r.Id}:{runDate:yyyyMMdd}", $"Recurring invoice “{r.Name}” could not be issued", run.Error, "/recurring", ct);
                return false;
            }
        }

        public async Task<RecurringDto> RunNowAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, id, ct);
            if (!r.IsActive) throw GatewayException.Conflict("SCHEDULE_PAUSED", "This schedule is paused or has ended. Resume it first.");
            if (!await RunOneAsync(r, ct)) { var failed = await _db.RecurringRuns.AsNoTracking().FirstAsync(x => x.RecurringInvoiceId == id && x.RunDate == r.NextRunDate.Date, ct); throw GatewayException.Conflict("RUN_FAILED", failed.Error ?? "The invoice could not be issued."); }
            return await ToDtoAsync(r, ct);
        }

        public async Task<int> RunDueAsync(DateTime catToday, CancellationToken ct)
        {
            var issued = 0;
            var today = catToday.Date;
            foreach (var id in await _db.RecurringInvoices.AsNoTracking().Where(r => r.IsActive && r.NextRunDate <= today).Select(r => r.Id).ToListAsync(ct))
            {
                for (var i = 0; i < 3; i++)   // catch up at most three missed dates per pass
                {
                    var r = await _db.RecurringInvoices.FirstAsync(x => x.Id == id, ct);
                    if (!r.IsActive || r.NextRunDate.Date > today || (r.EndDate != null && r.NextRunDate.Date > r.EndDate.Value.Date)) break;
                    if (!await RunOneAsync(r, ct)) break;
                    issued++;
                }
            }
            return issued;
        }
    }

    /// <summary>Issues due recurring invoices. Checks every 15 minutes (Zimbabwe date).</summary>
    public sealed class RecurringJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<RecurringJob> _log;
        public RecurringJob(IServiceScopeFactory scopes, ILogger<RecurringJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<IRecurringService>().RunDueAsync(FdmsClock.CatNow(), stop);
                    if (n > 0) _log.LogInformation("Issued {N} recurring invoice(s)", n);
                    await Task.Delay(TimeSpan.FromMinutes(15), stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Recurring invoice run failed"); try { await Task.Delay(TimeSpan.FromMinutes(30), stop); } catch (OperationCanceledException) { break; } }
            }
        }
    }

    // =================================================================================================================
    /// <summary>
    /// Sales and VAT for a period, laid out for completing a VAT return: per currency and tax rate, sales including VAT, VAT, sales excluding VAT, with credit and debit notes already netted
    /// off. It summarises the fiscal receipts; input VAT (what the company paid on purchases) is not known to the gateway.
    /// </summary>
    public sealed class VatReturnService : IVatReturnService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IReportService _reports;
        public VatReturnService(FdmsDbContext db, ICallerScope scope, IReportService reports) { _db = db; _scope = scope; _reports = reports; }

        public async Task<VatReturnDto> BuildAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct)
        {
            var sales = await _reports.SalesAsync(clientId, from, to, deviceId, ct);
            var devices = _db.Devices.AsNoTracking().InScope(_scope).Where(d => d.ClientId == clientId);
            if (deviceId != null) devices = devices.Where(d => d.Id == deviceId);
            var list = await devices.OrderBy(d => d.ZimraDeviceId).ToListAsync(ct);
            var first = list.FirstOrDefault(d => !string.IsNullOrEmpty(d.TaxPayerName)) ?? list.FirstOrDefault();
            var clientName = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
            var label = deviceId != null && first != null ? $"Device {first.ZimraDeviceId}" : list.Count == 1 ? $"Device {list[0].ZimraDeviceId}" : $"All devices ({list.Count})";

            var currencies = sales.Currencies.Select(c =>
            {
                var lines = c.Taxes.Select(t => new VatLineDto(t.Label, t.TaxPercent, t.Sales, t.Tax, t.Sales - t.Tax)).ToList();
                return new VatCurrencyDto(c.Currency, lines, lines.Sum(l => l.SalesInclusive), lines.Sum(l => l.Vat), lines.Sum(l => l.SalesExclusive), c.Invoices, c.InvoicesTotal, c.CreditNotes, c.CreditNotesTotal, c.DebitNotes, c.DebitNotesTotal);
            }).ToList();
            return new VatReturnDto(first?.TaxPayerName ?? clientName, first?.TaxPayerTin, first?.VatNumber, sales.From, sales.To, label, sales.Truncated, sales.Counts.Pending, sales.Counts.OfflineIssued, currencies, DateTime.UtcNow);
        }

        public async Task<byte[]> XlsxAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct)
        {
            var v = await BuildAsync(clientId, from, to, deviceId, ct);
            var sales = await _reports.SalesAsync(clientId, from, to, deviceId, ct);
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("VAT summary");
            ws.Cell(1, 1).Value = "VAT summary"; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 16;
            ws.Cell(2, 1).Value = "Taxpayer"; ws.Cell(2, 2).Value = v.TaxpayerName;
            ws.Cell(3, 1).Value = "TIN"; ws.Cell(3, 2).Value = v.Tin ?? "";
            ws.Cell(4, 1).Value = "VAT number"; ws.Cell(4, 2).Value = v.VatNumber ?? "";
            ws.Cell(5, 1).Value = "Period"; ws.Cell(5, 2).Value = $"{v.From} to {v.To}";
            ws.Cell(6, 1).Value = "Devices"; ws.Cell(6, 2).Value = v.DeviceLabel;
            var row = 8;
            foreach (var c in v.Currencies)
            {
                ws.Cell(row, 1).Value = c.Currency; ws.Cell(row, 1).Style.Font.Bold = true; row++;
                foreach (var (h, i) in new[] { "Tax rate", "Sales incl. VAT", "VAT (output tax)", "Sales excl. VAT" }.Select((h, i) => (h, i)))
                { var cell = ws.Cell(row, i + 1); cell.Value = h; cell.Style.Font.Bold = true; cell.Style.Fill.BackgroundColor = XLColor.LightGray; }
                row++;
                foreach (var l in c.Lines) { ws.Cell(row, 1).Value = l.Label; ws.Cell(row, 2).Value = l.SalesInclusive; ws.Cell(row, 3).Value = l.Vat; ws.Cell(row, 4).Value = l.SalesExclusive; row++; }
                ws.Cell(row, 1).Value = "Total"; ws.Cell(row, 2).Value = c.TotalSalesInclusive; ws.Cell(row, 3).Value = c.TotalVat; ws.Cell(row, 4).Value = c.TotalSalesExclusive;
                ws.Range(row, 1, row, 4).Style.Font.Bold = true; ws.Range(row, 1, row, 4).Style.Border.TopBorder = XLBorderStyleValues.Thin; row += 2;
                ws.Cell(row, 1).Value = $"Invoices: {c.Invoices} ({c.InvoicesTotal:N2})   Credit notes: {c.CreditNotes} ({c.CreditNotesTotal:N2})   Debit notes: {c.DebitNotes} ({c.DebitNotesTotal:N2})"; row += 2;
            }
            ws.Cell(row, 1).Value = "Credit notes are already deducted and debit notes added in the figures above. Only fiscalised, queued and offline-issued receipts are counted. Input VAT (purchases) is not included.";
            if (v.Pending + v.OfflineIssued > 0) { row++; ws.Cell(row, 1).Value = $"Includes {v.Pending} queued and {v.OfflineIssued} offline receipt(s) not yet confirmed by ZIMRA."; }
            if (v.Truncated) { row++; ws.Cell(row, 1).Value = "The period holds more receipts than can be summarised at once; choose a shorter period."; }
            ws.Range(1, 2, row, 4).Style.NumberFormat.Format = "#,##0.00"; ws.Column(1).Width = 20; ws.Columns(2, 4).Width = 20;

            var d = wb.AddWorksheet("Daily sales");
            d.Cell(1, 1).Value = "Date"; d.Cell(1, 2).Value = "Currency"; d.Cell(1, 3).Value = "Documents"; d.Cell(1, 4).Value = "Net sales"; d.Row(1).Style.Font.Bold = true;
            var r = 2; foreach (var p in sales.Days) { d.Cell(r, 1).Value = p.Date; d.Cell(r, 2).Value = p.Currency; d.Cell(r, 3).Value = p.Documents; d.Cell(r, 4).Value = p.Net; r++; }
            d.Column(4).Style.NumberFormat.Format = "#,##0.00"; d.Columns().AdjustToContents();
            using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
        }

        public async Task<byte[]> PdfAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct)
        {
            var v = await BuildAsync(clientId, from, to, deviceId, ct);
            QuestPDF.Settings.License = LicenseType.Community;
            var c = CultureInfo.InvariantCulture; string M(decimal x) => x.ToString("N2", c);
            return Document.Create(doc => doc.Page(page =>
            {
                page.Size(PageSizes.A4); page.Margin(40); page.DefaultTextStyle(t => t.FontSize(10));
                page.Content().Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Text("VAT summary").Bold().FontSize(22);
                    col.Item().Text($"{v.TaxpayerName}").Bold().FontSize(13);
                    col.Item().Text($"TIN {v.Tin ?? "-"}   VAT No {v.VatNumber ?? "-"}");
                    col.Item().Text($"Period {v.From} to {v.To}   ·   {v.DeviceLabel}");
                    foreach (var cur in v.Currencies)
                    {
                        col.Item().PaddingTop(8).Text(cur.Currency).Bold().FontSize(13);
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(cd => { cd.RelativeColumn(2); cd.RelativeColumn(3); cd.RelativeColumn(3); cd.RelativeColumn(3); });
                            foreach (var h in new[] { "Tax rate", "Sales incl. VAT", "VAT (output tax)", "Sales excl. VAT" }) t.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text(h).Bold();
                            foreach (var l in cur.Lines) { t.Cell().Padding(4).Text(l.Label); t.Cell().Padding(4).AlignRight().Text(M(l.SalesInclusive)); t.Cell().Padding(4).AlignRight().Text(M(l.Vat)); t.Cell().Padding(4).AlignRight().Text(M(l.SalesExclusive)); }
                            t.Cell().BorderTop(1).Padding(4).Text("Total").Bold(); t.Cell().BorderTop(1).Padding(4).AlignRight().Text(M(cur.TotalSalesInclusive)).Bold();
                            t.Cell().BorderTop(1).Padding(4).AlignRight().Text(M(cur.TotalVat)).Bold(); t.Cell().BorderTop(1).Padding(4).AlignRight().Text(M(cur.TotalSalesExclusive)).Bold();
                        });
                        col.Item().Text($"Invoices {cur.Invoices} ({M(cur.InvoicesTotal)})   Credit notes {cur.CreditNotes} ({M(cur.CreditNotesTotal)})   Debit notes {cur.DebitNotes} ({M(cur.DebitNotesTotal)})").FontSize(9).FontColor(Colors.Grey.Darken1);
                    }
                    if (v.Currencies.Count == 0) col.Item().Text("No receipts in this period.");
                    col.Item().PaddingTop(10).Text("Credit notes are deducted and debit notes added. Only fiscalised, queued and offline-issued receipts are counted. Input VAT on purchases is not included. This summary helps you complete your VAT return; it is not a ZIMRA form.").FontSize(8).FontColor(Colors.Grey.Darken1);
                    if (v.Pending + v.OfflineIssued > 0) col.Item().Text($"Includes {v.Pending} queued and {v.OfflineIssued} offline receipt(s) not yet confirmed by ZIMRA.").FontSize(8).FontColor(Colors.Orange.Darken2);
                    col.Item().Text("Generated " + v.GeneratedAt.ToString("d MMM yyyy HH:mm", c) + " UTC").FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            })).GeneratePdf();
        }
    }
}
