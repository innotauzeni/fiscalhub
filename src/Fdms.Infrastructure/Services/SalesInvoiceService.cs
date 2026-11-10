using System.Globalization;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Billing;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Invoices and credit notes issued from the books, for companies on an accounting-only plan (they do not fiscalise, so they charge no VAT and the documents say they are not
    /// fiscal tax invoices). A draft can be changed freely; issuing numbers it and posts it (receivables against income, a credit note the other way). Payments are received
    /// under Customers owing, exactly like a fiscal invoice left on account.
    /// </summary>
    public sealed class SalesInvoiceService : ISalesInvoiceService
    {
        static SalesInvoiceService() => QuestPDF.Settings.License = LicenseType.Community;
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IAccountingService _books; private readonly IEmailSender _email;
        public SalesInvoiceService(FdmsDbContext db, ICallerScope scope, IAccountingService books, IEmailSender email) { _db = db; _scope = scope; _books = books; _email = email; }

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        private async Task<string?> RefusalAsync(Guid clientId, CancellationToken ct)
        {
            if (PlatformBooks.Is(clientId)) return "The platform's invoices to companies are issued under Billing.";
            return await PlanModuleAccess.KindAsync(_db, clientId, ct) == PlanKinds.Accounting ? null
                : "Your company fiscalises, so its invoices are issued as fiscal invoices (Receipts → New invoice) and reach the books by themselves.";
        }

        private async Task RequireAllowedAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            if (await RefusalAsync(clientId, ct) is { } why) throw GatewayException.Conflict("FISCAL_COMPANY", why);
        }

        public async Task<SalesSetupDto> SetupAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await _books.EnsureChartAsync(clientId, ct);
            var why = await RefusalAsync(clientId, ct);
            var sales = await ChartCodes.ByRoleAsync(_db, clientId, "sales", "4010", ct);
            var st = await _db.AccountingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            return new SalesSetupDto(why == null, why, sales?.Id, st?.CustomerTermsDays is > 0 and var d ? d : 30, st?.InvoiceFooter);
        }

        public async Task<SalesSetupDto> SaveFooterAsync(Guid clientId, SaveInvoiceFooterRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var text = string.IsNullOrWhiteSpace(req.Footer) ? null : req.Footer.Trim();
            if (text is { Length: > 500 }) throw GatewayException.BadRequest("TOO_LONG", "The invoice footer can be at most 500 characters.");
            var row = await _db.AccountingSettings.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (row == null) _db.AccountingSettings.Add(row = new AccountingSetting { ClientId = clientId });
            row.InvoiceFooter = text; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            await _db.SaveChangesAsync(ct);
            return await SetupAsync(clientId, ct);
        }

        // ---- reading ------------------------------------------------------------------------------------------------------------

        private async Task<List<SalesInvoiceDto>> MapAsync(Guid clientId, List<SalesInvoice> docs, CancellationToken ct)
        {
            if (docs.Count == 0) return new();
            var ids = docs.Select(d => d.Id).ToList();
            var paid = (await _db.ArPayments.AsNoTracking().Where(p => p.ClientId == clientId && !p.IsVoid && ids.Contains(p.ReceiptId)).GroupBy(p => p.ReceiptId)
                .Select(g => new { g.Key, A = g.Sum(x => x.Amount) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.A);
            var credited = (await _db.SalesInvoices.AsNoTracking().Where(c => c.ClientId == clientId && c.Kind == "credit_note" && c.Status == "Issued" && c.OriginalId != null && ids.Contains(c.OriginalId.Value))
                .GroupBy(c => c.OriginalId!.Value).Select(g => new { g.Key, A = g.Sum(x => x.Total) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.A);
            var originals = docs.Where(d => d.OriginalId != null).Select(d => d.OriginalId!.Value).Distinct().ToList();
            var originalNo = await _db.SalesInvoices.AsNoTracking().Where(d => originals.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Number, ct);
            var accIds = docs.SelectMany(d => d.Lines.Select(l => l.AccountId)).Distinct().ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => accIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code + " " + a.Name, ct);
            var entries = docs.Where(d => d.JournalEntryId != null).Select(d => d.JournalEntryId!.Value).ToList();
            var jv = await _db.JournalEntries.AsNoTracking().Where(e => entries.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
            return docs.Select(d =>
            {
                var p = paid.GetValueOrDefault(d.Id); var c = credited.GetValueOrDefault(d.Id);
                var owing = d.Status == "Issued" && d.Kind != "credit_note" ? d.Total - p - c : 0;
                return new SalesInvoiceDto(d.Id, d.Kind, d.Number, d.Status, d.CustomerId, d.CustomerName, d.CustomerTin, d.CustomerEmail, d.Date, d.DueDate, d.Currency, d.Reference, d.Notes,
                    d.Subtotal, d.Discount, d.Total, c, p, owing, owing > 0 && d.DueDate.Date < Today ? (int)(Today - d.DueDate.Date).TotalDays : 0,
                    d.OriginalId, d.OriginalId is { } o ? originalNo.GetValueOrDefault(o) : null, d.JournalEntryId is { } j ? jv.GetValueOrDefault(j) : null, d.CreatedBy, d.CreatedAt,
                    d.Lines.OrderBy(l => l.Sort).Select(l => new SalesInvoiceLineDto(l.Id, l.ProductId, l.Description, l.Quantity, l.UnitPrice, l.DiscountPercent, l.Amount, l.AccountId,
                        accs.GetValueOrDefault(l.AccountId, ""), l.CostCentreId)).ToList());
            }).ToList();
        }

        public async Task<IReadOnlyList<SalesInvoiceDto>> ListAsync(Guid clientId, SalesInvoiceFilter f, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var q = _db.SalesInvoices.AsNoTracking().Include(d => d.Lines).Where(d => d.ClientId == clientId);
            if (!string.IsNullOrWhiteSpace(f.Kind)) q = q.Where(d => d.Kind == f.Kind);
            if (f.CustomerId is { } cid) q = q.Where(d => d.CustomerId == cid);
            if (!string.IsNullOrWhiteSpace(f.Currency)) { var c = f.Currency.Trim().ToUpperInvariant(); q = q.Where(d => d.Currency == c); }
            if (f.From is { } from) q = q.Where(d => d.Date >= from.Date);
            if (f.To is { } to) q = q.Where(d => d.Date <= to.Date);
            if (!string.IsNullOrWhiteSpace(f.Q)) { var s = f.Q.Trim(); q = q.Where(d => (d.Number != null && d.Number.Contains(s)) || d.CustomerName.Contains(s) || (d.Reference != null && d.Reference.Contains(s))); }
            var status = (f.Status ?? "").Trim().ToLowerInvariant();
            if (status == "draft") q = q.Where(d => d.Status == "Draft");
            else if (status == "void") q = q.Where(d => d.Status == "Void");
            else if (status is "open" or "overdue" or "paid") q = q.Where(d => d.Status == "Issued" && d.Kind != "credit_note");
            var rows = await q.OrderByDescending(d => d.Date).ThenByDescending(d => d.CreatedAt).Take(Math.Clamp(f.Take, 1, 1000)).ToListAsync(ct);
            var list = await MapAsync(clientId, rows, ct);
            return status switch
            {
                "open" => list.Where(d => d.Outstanding > 0).ToList(), "overdue" => list.Where(d => d.DaysOverdue > 0).ToList(), "paid" => list.Where(d => d.Outstanding <= 0).ToList(), _ => list,
            };
        }

        private async Task<SalesInvoice> LoadAsync(Guid clientId, Guid id, CancellationToken ct) =>
            await _db.SalesInvoices.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == id && d.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Invoice");

        public async Task<SalesInvoiceDto> GetAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var d = await _db.SalesInvoices.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Invoice");
            return (await MapAsync(clientId, new List<SalesInvoice> { d }, ct))[0];
        }

        // ---- drafts -------------------------------------------------------------------------------------------------------------

        private static string? Clean(string? v, int max, string what)
        {
            v = v?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            return v.Length > max ? throw GatewayException.BadRequest("TOO_LONG", $"{what} can be at most {max} characters.") : v;
        }

        public async Task<SalesInvoiceDto> SaveAsync(Guid clientId, SaveSalesInvoiceRequest req, CancellationToken ct)
        {
            await RequireAllowedAsync(clientId, ct);
            await _books.EnsureChartAsync(clientId, ct);
            var kind = req.Kind is "credit_note" ? "credit_note" : req.Kind is "invoice" or null or "" ? "invoice" : throw GatewayException.BadRequest("KIND_INVALID", "A document is an invoice or a credit note.");
            var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CustomerId && c.ClientId == clientId, ct) ?? throw GatewayException.BadRequest("CUSTOMER_INVALID", "Choose the customer.");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (req.Date.Year < 2000 || req.Date.Date > Today.AddDays(366)) throw GatewayException.BadRequest("DATE_INVALID", "That date is not valid.");
            var terms = (await SetupAsync(clientId, ct)).TermsDays;
            var due = (req.DueDate ?? req.Date.AddDays(terms)).Date;
            if (due < req.Date.Date) throw GatewayException.BadRequest("DATE_INVALID", "The due date is before the invoice date.");

            SalesInvoice? original = null;
            if (kind == "credit_note")
            {
                original = await _db.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == req.OriginalId && d.ClientId == clientId, ct)
                    ?? throw GatewayException.BadRequest("ORIGINAL_REQUIRED", "Choose the invoice this credit note reduces.");
                if (original.Kind == "credit_note" || original.Status != "Issued") throw GatewayException.BadRequest("ORIGINAL_INVALID", "A credit note reduces an issued invoice.");
                if (original.CustomerId != customer.Id || original.Currency != cur) throw GatewayException.BadRequest("ORIGINAL_INVALID", "A credit note is for the same customer and currency as its invoice.");
                if (req.Date.Date < original.Date.Date) throw GatewayException.BadRequest("DATE_INVALID", "A credit note cannot be dated before its invoice.");
            }

            var input = (req.Lines ?? Array.Empty<SalesInvoiceLineInput>()).Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.UnitPrice != 0).ToList();
            if (input.Count == 0) throw GatewayException.BadRequest("LINES_REQUIRED", "Add at least one line.");
            if (input.Count > 200) throw GatewayException.BadRequest("LINES_TOO_MANY", "At most 200 lines.");
            var defaultRole = kind == "credit_note" ? "sales_returns" : "sales";
            var fallback = await ChartCodes.ByRoleAsync(_db, clientId, defaultRole, kind == "credit_note" ? "4020" : "4010", ct) ?? await ChartCodes.ByRoleAsync(_db, clientId, "sales", "4010", ct)
                ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The sales account is missing from your chart of accounts.");
            var accIds = input.Where(l => l.AccountId != null).Select(l => l.AccountId!.Value).Distinct().ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && accIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            await _books.CheckCentresAsync(clientId, input.Select(l => l.CostCentreId), ct);
            var productIds = input.Where(l => l.ProductId != null).Select(l => l.ProductId!.Value).Distinct().ToList();
            var products = await _db.Products.AsNoTracking().Where(p => p.ClientId == clientId && productIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);

            var lines = new List<SalesInvoiceLine>(); decimal gross = 0;
            for (var i = 0; i < input.Count; i++)
            {
                var l = input[i]; var n = i + 1;
                var text = (l.Description ?? "").Trim();
                if (text.Length is < 1 or > 300) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n}: describe what was sold (up to 300 characters).");
                if (l.Quantity <= 0 || l.Quantity > 1_000_000_000m) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n}: the quantity must be above zero.");
                if (l.UnitPrice < 0 || l.UnitPrice > 1_000_000_000m) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n}: the price cannot be negative.");
                if (l.DiscountPercent is < 0 or > 100) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n}: the discount is between 0 and 100%.");
                var account = fallback.Id;
                if (l.AccountId is { } aid)
                {
                    if (!accs.TryGetValue(aid, out var a) || a.Type != "Income" || a.IsGroup || !a.IsActive) throw GatewayException.BadRequest("ACCOUNT_INVALID", $"Line {n}: choose an income account (not a heading).");
                    account = a.Id;
                }
                if (l.ProductId is { } pid && !products.Contains(pid)) throw GatewayException.BadRequest("PRODUCT_INVALID", $"Line {n}: that product is not yours.");
                var full = Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero);
                var amount = Math.Round(l.Quantity * l.UnitPrice * (1 - l.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);
                gross += full;
                lines.Add(new SalesInvoiceLine { ClientId = clientId, Sort = n, ProductId = l.ProductId, Description = text, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, Amount = amount,
                    AccountId = account, CostCentreId = l.CostCentreId });
            }
            var total = lines.Sum(l => l.Amount);
            if (total <= 0) throw GatewayException.BadRequest("AMOUNT_INVALID", "The total must be above zero.");

            SalesInvoice doc;
            if (req.Id is { } id && id != Guid.Empty)
            {
                doc = await LoadAsync(clientId, id, ct);
                if (doc.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", "Only a draft can be changed. Issue a credit note to correct an issued invoice.");
                if (doc.Kind != kind) throw GatewayException.BadRequest("KIND_INVALID", "A draft keeps its kind.");
                _db.SalesInvoiceLines.RemoveRange(doc.Lines);
                doc.Lines = new();
            }
            else
            {
                doc = new SalesInvoice { ClientId = clientId, Kind = kind, CreatedBy = _scope.UserName };
                _db.SalesInvoices.Add(doc);
            }
            doc.CustomerId = customer.Id; doc.CustomerName = customer.Name; doc.CustomerTin = customer.Tin; doc.CustomerEmail = customer.Email; doc.CustomerAddress = customer.Address;
            doc.Date = req.Date.Date; doc.DueDate = kind == "credit_note" ? req.Date.Date : due; doc.Currency = cur; doc.OriginalId = original?.Id;
            doc.Reference = Clean(req.Reference, 60, "The reference"); doc.Notes = Clean(req.Notes, 1000, "The notes");
            doc.Subtotal = gross; doc.Total = total; doc.Discount = gross - total;
            foreach (var l in lines) { l.InvoiceId = doc.Id; doc.Lines.Add(l); _db.SalesInvoiceLines.Add(l); }
            await _db.SaveChangesAsync(ct);
            return await GetAsync(clientId, doc.Id, ct);
        }

        public async Task DeleteDraftAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var doc = await LoadAsync(clientId, id, ct);
            if (doc.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", "An issued document is never deleted: void it instead.");
            _db.SalesInvoices.Remove(doc);
            await _db.SaveChangesAsync(ct);
        }

        // ---- issuing and voiding --------------------------------------------------------------------------------------------------

        public async Task<SalesInvoiceDto> IssueAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            await RequireAllowedAsync(clientId, ct);
            var doc = await LoadAsync(clientId, id, ct);
            if (doc.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", "That document is already issued.");
            var receivable = await ChartCodes.ByRoleAsync(_db, clientId, "receivable", "1100", ct) is { IsActive: true } r ? r
                : throw GatewayException.Conflict("ACCOUNT_MISSING", "The accounts receivable account is missing or switched off in your chart of accounts.");
            var credit = doc.Kind == "credit_note";
            if (credit)
            {
                var orig = (await MapAsync(clientId, await _db.SalesInvoices.AsNoTracking().Include(d => d.Lines).Where(d => d.Id == doc.OriginalId).ToListAsync(ct), ct)).FirstOrDefault()
                    ?? throw GatewayException.Conflict("ORIGINAL_INVALID", "The invoice this credit note reduces is gone.");
                if (orig.Status != "Issued") throw GatewayException.Conflict("ORIGINAL_INVALID", "The invoice this credit note reduces is no longer issued.");
                if (doc.Total > orig.Outstanding) throw GatewayException.Conflict("CREDIT_TOO_HIGH", $"Only {orig.Outstanding:N2} {orig.Currency} is still owing on {orig.Number}: a credit note cannot be for more.");
            }
            var prefix = credit ? "CN-" : "INV-";
            var seq = (await _db.SalesInvoices.Where(d => d.ClientId == clientId && d.Kind == doc.Kind).MaxAsync(d => (int?)d.Seq, ct) ?? 0) + 1;
            doc.Seq = seq; doc.Number = prefix + seq.ToString("D6");

            // receivables against income per line (a credit note the other way round)
            var lines = new List<JournalLineInput> { new(receivable.Id, credit ? 0 : doc.Total, credit ? doc.Total : 0, doc.CustomerName) };
            foreach (var g in doc.Lines.GroupBy(l => (l.AccountId, l.CostCentreId)))
            {
                var amt = g.Sum(l => l.Amount);
                if (amt == 0) continue;
                lines.Add(new JournalLineInput(g.Key.AccountId, credit ? amt : 0, credit ? 0 : amt, g.Count() == 1 ? g.First().Description : null, g.Key.CostCentreId));
            }
            var label = credit ? "Credit note" : "Invoice";
            var entry = await _books.PostLinesAsync(clientId, doc.Date, doc.Currency, $"{label} {doc.Number} - {doc.CustomerName}", doc.Reference ?? doc.Number, credit ? "credit_note" : "invoice", doc.Id, lines, ct);
            doc.JournalEntryId = entry.Id; doc.Status = "Issued"; doc.IssuedAt = DateTime.UtcNow;
            if (doc.QuoteId is { } qid && await _db.Quotes.FirstOrDefaultAsync(x => x.Id == qid && x.ClientId == clientId, ct) is { } quote) { quote.Status = "Invoiced"; quote.UpdatedAt = DateTime.UtcNow; }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = credit ? "sales.credit_note.issue" : "sales.invoice.issue", Entity = "SalesInvoice", EntityId = doc.Id.ToString(),
                Detail = $"{doc.Number} {doc.CustomerName} {doc.Total:0.00} {doc.Currency}" });
            await _db.SaveChangesAsync(ct);
            return await GetAsync(clientId, doc.Id, ct);
        }

        public async Task<SalesInvoiceDto> VoidAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var doc = await LoadAsync(clientId, id, ct);
            if (doc.Status == "Draft") throw GatewayException.Conflict("NOT_ISSUED", "A draft is deleted, not voided.");
            if (doc.Status == "Void") throw GatewayException.Conflict("ALREADY_VOID", "That document is already void.");
            if (await _db.ArPayments.AnyAsync(p => p.ReceiptId == id && !p.IsVoid, ct)) throw GatewayException.Conflict("HAS_PAYMENTS", "Payments were received against it: void them under Customers owing first.");
            if (await _db.SalesInvoices.AnyAsync(c => c.OriginalId == id && c.Status == "Issued", ct)) throw GatewayException.Conflict("HAS_CREDIT_NOTES", "Credit notes stand against it: void them first.");
            if (doc.JournalEntryId is { } eid) await _books.ReverseJournalAsync(clientId, eid, ct);
            doc.Status = "Void"; doc.VoidedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "sales.void", Entity = "SalesInvoice", EntityId = doc.Id.ToString(), Detail = $"{doc.Number} {doc.CustomerName}" });
            await _db.SaveChangesAsync(ct);
            return await GetAsync(clientId, id, ct);
        }

        public async Task<SalesInvoiceDto> FromQuoteAsync(Guid clientId, Guid quoteId, CancellationToken ct)
        {
            await RequireAllowedAsync(clientId, ct);
            var q = await _db.Quotes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == quoteId && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Quote");
            if (q.Status == "Declined") throw GatewayException.Conflict("QUOTE_DECLINED", "The customer declined this quote.");
            if (await _db.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(d => d.ClientId == clientId && d.QuoteId == quoteId && d.Status != "Void", ct) is { } done)
                return await GetAsync(clientId, done.Id, ct);   // asked twice: the same invoice
            var name = q.CustomerName.Trim();
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.ClientId == clientId && ((q.CustomerTin != null && c.Tin == q.CustomerTin) || c.Name == name), ct);
            if (customer == null)
            {
                customer = new Customer { ClientId = clientId, Name = name.Length > 0 ? name : "Customer", Tin = q.CustomerTin, VatNumber = q.CustomerVat, Email = q.CustomerEmail, Phone = q.CustomerPhone };
                _db.Customers.Add(customer); await _db.SaveChangesAsync(ct);
            }
            var lines = LineTools.Read(q.LinesJson).Select(l => new SalesInvoiceLineInput(null, l.Name, l.Quantity, l.UnitPrice)).ToList();
            var dto = await SaveAsync(clientId, new SaveSalesInvoiceRequest(null, customer.Id, Today, null, q.Currency, q.Number, q.Notes, lines), ct);
            var doc = await LoadAsync(clientId, dto.Id, ct);
            doc.QuoteId = q.Id; await _db.SaveChangesAsync(ct);
            return dto;
        }

        // ---- the document -------------------------------------------------------------------------------------------------------

        public async Task<(byte[] Pdf, string FileName)> PdfAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var d = await GetAsync(clientId, id, ct);
            var me = await CompanyIdentity.LoadAsync(_db, clientId, ct);
            var footer = (await _db.AccountingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct))?.InvoiceFooter;
            var address = await _db.SalesInvoices.AsNoTracking().Where(x => x.Id == id).Select(x => x.CustomerAddress).FirstOrDefaultAsync(ct);
            var c = CultureInfo.InvariantCulture;
            string M(decimal v) => v.ToString("N2", c);
            string Q(decimal v) => v.ToString("0.####", c);
            string D(DateTime x) => x.ToString("d MMM yyyy", c);
            const string accent = "#0f766e";
            var title = d.Kind == "credit_note" ? "CREDIT NOTE" : "INVOICE";
            var pdf = Document.Create(doc => doc.Page(page =>
            {
                page.Size(PageSizes.A4); page.Margin(36); page.DefaultTextStyle(t => t.FontSize(9.5f));
                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(me.Name).Bold().FontSize(15).FontColor(accent);
                        if (me.Address != null) col.Item().Text(me.Address);
                        if (me.Tin != null) col.Item().Text("TIN " + me.Tin);
                        if (me.Email != null) col.Item().Text(me.Email);
                    });
                    row.ConstantItem(210).Column(col =>
                    {
                        col.Item().AlignRight().Text(d.Status == "Draft" ? "DRAFT " + title : title).Bold().FontSize(18).FontColor(accent);
                        col.Item().AlignRight().Text(d.Number ?? "Not issued yet").SemiBold();
                        col.Item().AlignRight().Text("Date " + D(d.Date));
                        if (d.Kind != "credit_note") col.Item().AlignRight().Text("Due " + D(d.DueDate));
                        if (d.OriginalNumber != null) col.Item().AlignRight().Text("Against invoice " + d.OriginalNumber);
                        if (d.Reference != null && d.Reference != d.Number) col.Item().AlignRight().Text("Reference " + d.Reference);
                    });
                });
                page.Content().PaddingTop(16).Column(col =>
                {
                    col.Spacing(12);
                    col.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(p =>
                    {
                        p.Item().Text(d.Kind == "credit_note" ? "Credited to" : "Bill to").FontColor(Colors.Grey.Darken1).FontSize(8);
                        p.Item().Text(d.Customer).Bold().FontSize(11);
                        if (!string.IsNullOrWhiteSpace(address)) p.Item().Text(address);
                        if (d.CustomerTin != null) p.Item().Text("TIN " + d.CustomerTin);
                    });
                    var discount = d.Lines.Any(l => l.DiscountPercent != 0);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(56); cd.ConstantColumn(70); if (discount) cd.ConstantColumn(50); cd.ConstantColumn(80); });
                        void H(string s, bool right = true) { var cell = t.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4); (right ? cell.AlignRight() : cell).Text(s).Bold(); }
                        H("Description", false); H("Qty"); H("Price"); if (discount) H("Disc."); H("Amount");
                        foreach (var l in d.Lines)
                        {
                            IContainer Cell() => t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4);
                            Cell().Text(l.Description); Cell().AlignRight().Text(Q(l.Quantity)); Cell().AlignRight().Text(M(l.UnitPrice));
                            if (discount) Cell().AlignRight().Text(l.DiscountPercent == 0 ? "" : l.DiscountPercent.ToString("0.##", c) + "%");
                            Cell().AlignRight().Text(M(l.Amount));
                        }
                    });
                    col.Item().AlignRight().Width(230).Column(tot =>
                    {
                        void Line(string label, string value, bool bold = false)
                        {
                            tot.Item().Row(r => { var a = r.RelativeItem().Text(label); var b = r.ConstantItem(100).AlignRight().Text(value); if (bold) { a.Bold(); b.Bold(); } });
                        }
                        if (d.Discount != 0) { Line("Subtotal", M(d.Subtotal)); Line("Discount", "-" + M(d.Discount)); }
                        tot.Item().PaddingTop(2).BorderTop(1).BorderColor(Colors.Grey.Medium);
                        Line($"Total {d.Currency}", M(d.Total), true);
                        if (d.Kind != "credit_note" && d.Status == "Issued")
                        {
                            if (d.Credited != 0) Line("Credited", "-" + M(d.Credited));
                            if (d.Paid != 0) Line("Paid", "-" + M(d.Paid));
                            Line("Amount due", M(d.Outstanding), true);
                        }
                    });
                    if (!string.IsNullOrWhiteSpace(d.Notes)) col.Item().Text(d.Notes);
                    if (!string.IsNullOrWhiteSpace(footer)) col.Item().PaddingTop(6).Text(footer).FontColor(Colors.Grey.Darken2);
                });
                page.Footer().Column(f =>
                {
                    f.Item().AlignCenter().Text("This is not a fiscal tax invoice. No VAT has been charged.").FontSize(8).FontColor(Colors.Grey.Darken1);
                    f.Item().AlignCenter().Text(x => { x.Span("Page ").FontSize(8); x.CurrentPageNumber().FontSize(8); x.Span(" of ").FontSize(8); x.TotalPages().FontSize(8); });
                });
            })).GeneratePdf();
            var name = Regex.Replace(d.Number ?? "draft", "[^A-Za-z0-9_-]+", "-");
            return (pdf, $"{(d.Kind == "credit_note" ? "credit-note" : "invoice")}-{name}.pdf");
        }

        public async Task<EmailInvoiceResult> EmailAsync(Guid clientId, Guid id, EmailInvoiceRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var d = await GetAsync(clientId, id, ct);
            if (d.Status != "Issued") throw GatewayException.Conflict("NOT_ISSUED", "Issue the document before sending it.");
            var to = string.IsNullOrWhiteSpace(req.Email) ? d.CustomerEmail : req.Email.Trim();
            if (to == null || !Regex.IsMatch(to, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw GatewayException.BadRequest("EMAIL_REQUIRED", $"There is no email address for {d.Customer}. Type one.");
            if (!_email.Configured) throw GatewayException.Conflict("EMAIL_NOT_SET_UP", "Email is not set up on this server. Download the PDF instead.");
            var (pdf, file) = await PdfAsync(clientId, id, ct);
            var me = await CompanyIdentity.LoadAsync(_db, clientId, ct);
            string H(string s) => System.Net.WebUtility.HtmlEncode(s);
            var what = d.Kind == "credit_note" ? "credit note" : "invoice";
            var note = string.IsNullOrWhiteSpace(req.Message) ? "" : $"<p>{H(req.Message.Trim())}</p>";
            var due = d.Kind == "credit_note" ? "" : $"<p>Amount due: <b>{d.Outstanding:N2} {d.Currency}</b> by {d.DueDate:d MMM yyyy}.</p>";
            var html = $"<p>Dear {H(d.Customer)},</p>{note}<p>Please find attached {what} {H(d.Number ?? "")} from {H(me.Name)}.</p>{due}<p>Kind regards,<br/>{H(me.Name)}</p>";
            if (await _email.TrySendAsync(to, $"{(d.Kind == "credit_note" ? "Credit note" : "Invoice")} {d.Number} from {me.Name}", html, new[] { new EmailAttachment(file, pdf, "application/pdf") }, ct) is { } refused)
                throw GatewayException.Conflict("EMAIL_FAILED", "The mail server did not take the message: " + refused);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "sales.email", Entity = "SalesInvoice", EntityId = id.ToString(), Detail = $"{d.Number} → {to}" });
            await _db.SaveChangesAsync(ct);
            return new EmailInvoiceResult(to);
        }
    }
}
