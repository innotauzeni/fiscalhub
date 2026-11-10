using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    // =================================================================================================================
    public sealed class CustomerService : ICustomerService
    {
        private static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        public CustomerService(FdmsDbContext db) => _db = db;

        private static CustomerDto ToDto(Customer c) => new(c.Id, c.Name, c.TradeName, c.Tin, c.VatNumber, c.Email, c.Phone, c.Address, c.Notes, c.IsActive, c.CreatedAt);
        private static string? Clean(string? s, int max, string field)
        {
            var v = s?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (v.Length > max) throw GatewayException.BadRequest("FIELD_TOO_LONG", $"{field} may be at most {max} characters.");
            return v;
        }

        public async Task<IReadOnlyList<CustomerDto>> ListAsync(Guid clientId, string? search, bool includeInactive, CancellationToken ct)
        {
            var q = _db.Customers.AsNoTracking().Where(c => c.ClientId == clientId && (includeInactive || c.IsActive));
            var term = search?.Trim();
            if (!string.IsNullOrEmpty(term)) q = q.Where(c => c.Name.Contains(term) || (c.TradeName != null && c.TradeName.Contains(term)) || (c.Tin != null && c.Tin.Contains(term)) || (c.Email != null && c.Email.Contains(term)));
            return (await q.OrderBy(c => c.Name).Take(500).ToListAsync(ct)).Select(ToDto).ToList();
        }

        public async Task<CustomerDto> SaveAsync(Guid clientId, SaveCustomerRequest r, CancellationToken ct)
        {
            var name = r.Name?.Trim() ?? "";
            if (name.Length < 2 || name.Length > 150) throw GatewayException.BadRequest("NAME_INVALID", "Enter the customer's registered name (2-150 characters).");
            var tin = Clean(r.Tin, 20, "TIN");
            if (tin != null && !Regex.IsMatch(tin, @"^\d{10}$")) throw GatewayException.BadRequest("TIN_INVALID", "A ZIMRA TIN is 10 digits.");
            var vat = Clean(r.VatNumber, 20, "VAT number");
            if (vat != null && !Regex.IsMatch(vat, @"^\d{9}$")) throw GatewayException.BadRequest("VAT_INVALID", "A VAT number is 9 digits.");
            var email = Clean(r.Email, 200, "Email");
            if (email != null && !EmailRx.IsMatch(email)) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter a valid email address.");
            if (tin != null && await _db.Customers.AnyAsync(c => c.ClientId == clientId && c.Tin == tin && c.Id != r.Id, ct))
                throw GatewayException.Conflict("CUSTOMER_EXISTS", "A customer with that TIN already exists.");

            var c = r.Id is { } id && id != Guid.Empty ? await _db.Customers.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Customer") : new Customer { ClientId = clientId };
            c.Name = name; c.TradeName = Clean(r.TradeName, 150, "Trade name"); c.Tin = tin; c.VatNumber = vat; c.Email = email; c.Phone = Clean(r.Phone, 40, "Phone");
            c.Address = Clean(r.Address, 300, "Address"); c.Notes = Clean(r.Notes, 500, "Notes"); c.IsActive = r.IsActive; c.UpdatedAt = DateTime.UtcNow;
            if (_db.Entry(c).State == EntityState.Detached) _db.Customers.Add(c);
            await _db.SaveChangesAsync(ct);
            return ToDto(c);
        }

        public async Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var c = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Customer");
            _db.Customers.Remove(c);   // invoices keep their own copy of the buyer's details, so nothing refers back to this row
            await _db.SaveChangesAsync(ct);
        }
    }

    // =================================================================================================================
    public sealed class ProductService : IProductService
    {
        private readonly FdmsDbContext _db;
        public ProductService(FdmsDbContext db) => _db = db;

        private static ProductDto ToDto(Product p) => new(p.Id, p.Code, p.Name, p.HsCode, p.Unit, p.UnitPrice, p.Currency, p.TaxPercent, p.IsExempt, p.IsActive, p.CreatedAt);

        public async Task<IReadOnlyList<ProductDto>> ListAsync(Guid clientId, string? search, bool includeInactive, CancellationToken ct)
        {
            var q = _db.Products.AsNoTracking().Where(p => p.ClientId == clientId && (includeInactive || p.IsActive));
            var term = search?.Trim();
            if (!string.IsNullOrEmpty(term)) q = q.Where(p => p.Name.Contains(term) || (p.Code != null && p.Code.Contains(term)) || (p.HsCode != null && p.HsCode.Contains(term)));
            return (await q.OrderBy(p => p.Name).Take(1000).ToListAsync(ct)).Select(ToDto).ToList();
        }

        public async Task<ProductDto> SaveAsync(Guid clientId, SaveProductRequest r, CancellationToken ct)
        {
            var name = r.Name?.Trim() ?? "";
            if (name.Length < 2 || name.Length > 200) throw GatewayException.BadRequest("NAME_INVALID", "Enter the item name (2-200 characters).");
            var code = string.IsNullOrWhiteSpace(r.Code) ? null : r.Code.Trim();
            if (code != null && code.Length > 40) throw GatewayException.BadRequest("CODE_INVALID", "The item code may be at most 40 characters.");
            var hs = string.IsNullOrWhiteSpace(r.HsCode) ? null : r.HsCode.Trim();
            if (hs != null && !Regex.IsMatch(hs, @"^(?:\d{4}|\d{6}|\d{8})$")) throw GatewayException.BadRequest("HS_CODE_INVALID", "An HS code is 4, 6 or 8 digits (ZIMRA tariff codes are 8).");
            if (r.UnitPrice < 0 || r.UnitPrice > 1_000_000_000m) throw GatewayException.BadRequest("PRICE_INVALID", "Enter a unit price of zero or more.");
            var currency = string.IsNullOrWhiteSpace(r.Currency) ? "USD" : r.Currency.Trim().ToUpperInvariant();
            if (!Regex.IsMatch(currency, @"^[A-Z]{3}$")) throw GatewayException.BadRequest("CURRENCY_INVALID", "The currency is a 3-letter code such as USD or ZWG.");
            if (r.TaxPercent is < 0 or > 100) throw GatewayException.BadRequest("TAX_INVALID", "A tax rate is between 0 and 100.");
            var unit = string.IsNullOrWhiteSpace(r.Unit) ? null : r.Unit.Trim();
            if (unit != null && unit.Length > 20) throw GatewayException.BadRequest("UNIT_INVALID", "The unit may be at most 20 characters.");
            if (code != null && await _db.Products.AnyAsync(p => p.ClientId == clientId && p.Code == code && p.Id != r.Id, ct)) throw GatewayException.Conflict("PRODUCT_EXISTS", "Another item already uses that code.");

            var p = r.Id is { } id && id != Guid.Empty ? await _db.Products.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Item") : new Product { ClientId = clientId };
            p.Code = code; p.Name = name; p.HsCode = hs; p.Unit = unit; p.UnitPrice = decimal.Round(r.UnitPrice, 4); p.Currency = currency;
            p.IsExempt = r.IsExempt; p.TaxPercent = r.IsExempt ? null : (r.TaxPercent.HasValue ? decimal.Round(r.TaxPercent.Value, 2) : null); p.IsActive = r.IsActive; p.UpdatedAt = DateTime.UtcNow;
            if (_db.Entry(p).State == EntityState.Detached) _db.Products.Add(p);
            await _db.SaveChangesAsync(ct);
            return ToDto(p);
        }

        public async Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Item");
            if (await _db.StockMovements.AnyAsync(m => m.ProductId == id, ct)) throw GatewayException.Conflict("HAS_STOCK_HISTORY", "This item has stock history, so it is kept: switch it off instead.");
            _db.Products.Remove(p);
            await _db.SaveChangesAsync(ct);
        }
    }

    // =================================================================================================================
    /// <summary>
    /// Sales and VAT summaries built from what was actually sent to ZIMRA (the receipt payloads), limited to the caller's devices. Credit notes count against
    /// sales; rejected, cancelled and held receipts never count. Dates are Zimbabwe dates, like the receipts themselves.
    /// </summary>
    public sealed class ReportService : IReportService
    {
        private const int MaxReceipts = 20000, MaxDays = 400;
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        public ReportService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        private sealed record Row(Guid DeviceId, int ZimraId, string Currency, ReceiptType Type, DateTime Date, ReceiptPayload? Payload, decimal Total);

        private static (DateTime From, DateTime To) Range(DateTime from, DateTime to)
        {
            var f = from.Date; var t = to.Date;
            if (t < f) throw GatewayException.BadRequest("RANGE_INVALID", "The end date is before the start date.");
            if ((t - f).TotalDays > MaxDays) throw GatewayException.BadRequest("RANGE_TOO_LONG", $"Choose a period of at most {MaxDays} days.");
            return (f, t);
        }

        private async Task<List<Device>> DevicesAsync(Guid clientId, Guid? deviceId, CancellationToken ct)
        {
            var q = _db.Devices.AsNoTracking().InScope(_scope).Where(d => d.ClientId == clientId);
            if (deviceId.HasValue) q = q.Where(d => d.Id == deviceId);
            var list = await q.ToListAsync(ct);
            if (deviceId.HasValue && list.Count == 0) throw GatewayException.NotFound("Device");
            return list;
        }

        private async Task<(List<Row> Rows, ReportCountsDto Counts, bool Truncated)> LoadAsync(Guid clientId, DateTime f, DateTime t, Guid? deviceId, CancellationToken ct)
        {
            var devices = await DevicesAsync(clientId, deviceId, ct);
            var ids = devices.Select(d => d.Id).ToList();
            var zimra = devices.ToDictionary(d => d.Id, d => d.ZimraDeviceId);
            var end = t.AddDays(1);
            var inRange = _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId) && r.ReceiptDate >= f && r.ReceiptDate < end);
            var status = await inRange.GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            int N(ReceiptStatus s) => status.GetValueOrDefault(s);
            var counts = new ReportCountsDto(N(ReceiptStatus.Submitted) + N(ReceiptStatus.Pending) + N(ReceiptStatus.OfflineIssued), N(ReceiptStatus.Pending), N(ReceiptStatus.OfflineIssued), N(ReceiptStatus.Rejected), N(ReceiptStatus.Cancelled), N(ReceiptStatus.Held));

            var counted = await inRange.Where(r => r.Status == ReceiptStatus.Submitted || r.Status == ReceiptStatus.Pending || r.Status == ReceiptStatus.OfflineIssued)
                .OrderBy(r => r.ReceiptDate).Select(r => new { r.DeviceId, r.Currency, r.Type, r.ReceiptDate, r.PayloadJson, r.Total }).Take(MaxReceipts + 1).ToListAsync(ct);
            var truncated = counted.Count > MaxReceipts;
            var rows = counted.Take(MaxReceipts).Select(r =>
            {
                ReceiptPayload? p = null;
                try { if (!string.IsNullOrEmpty(r.PayloadJson)) p = JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options); } catch (JsonException) { /* totals still count */ }
                return new Row(r.DeviceId, zimra.GetValueOrDefault(r.DeviceId), r.Currency, r.Type, r.ReceiptDate, p, r.Total);
            }).ToList();
            return (rows, counts, truncated);
        }

        /// <summary>A credit note is always money going back, whatever sign it was stored with.</summary>
        private static decimal Signed(ReceiptType type, decimal v) => type == ReceiptType.CreditNote ? -Math.Abs(v) : Math.Abs(v);

        private static string TaxLabel(decimal? pct) => pct.HasValue ? $"{pct.Value.ToString("0.##", CultureInfo.InvariantCulture)}%" : "Exempt";

        public async Task<SalesReportDto> SalesAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct)
        {
            var (f, t) = Range(from, to);
            var (rows, counts, truncated) = await LoadAsync(clientId, f, t, deviceId, ct);

            var currencies = rows.GroupBy(r => r.Currency).OrderBy(g => g.Key).Select(g =>
            {
                decimal Sum(ReceiptType ty) => g.Where(r => r.Type == ty).Sum(r => Math.Abs(r.Total));
                var taxes = g.Where(r => r.Payload != null).SelectMany(r => r.Payload!.ReceiptTaxes.Select(x => (Pct: x.TaxPercent, Sales: Signed(r.Type, x.SalesAmountWithTax), Tax: Signed(r.Type, x.TaxAmount))))
                    .GroupBy(x => x.Pct).OrderByDescending(x => x.Key ?? -1).Select(x => new TaxLineDto(TaxLabel(x.Key), x.Key, x.Sum(v => v.Sales), x.Sum(v => v.Tax))).ToList();
                var pays = g.Where(r => r.Payload != null).SelectMany(r => r.Payload!.ReceiptPayments.Select(x => (x.MoneyTypeCode, Amount: Signed(r.Type, x.PaymentAmount))))
                    .GroupBy(x => x.MoneyTypeCode).OrderBy(x => x.Key).Select(x => new PaymentLineDto(x.Key, x.Sum(v => v.Amount))).ToList();
                var inv = Sum(ReceiptType.FiscalInvoice); var cn = Sum(ReceiptType.CreditNote); var dn = Sum(ReceiptType.DebitNote);
                return new CurrencyReportDto(g.Key, g.Count(r => r.Type == ReceiptType.FiscalInvoice), inv, g.Count(r => r.Type == ReceiptType.CreditNote), cn, g.Count(r => r.Type == ReceiptType.DebitNote), dn,
                    inv + dn - cn, taxes.Sum(x => x.Tax), taxes, pays);
            }).ToList();

            var days = rows.GroupBy(r => (Date: r.Date.Date, r.Currency)).OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Currency)
                .Select(g => new DayPointDto(g.Key.Date.ToString("yyyy-MM-dd"), g.Key.Currency, g.Count(), g.Sum(r => Signed(r.Type, r.Total)))).ToList();
            return new SalesReportDto(f.ToString("yyyy-MM-dd"), t.ToString("yyyy-MM-dd"), truncated, counts, currencies, days);
        }

        private static string Csv(IEnumerable<string?> cells) => string.Join(',', cells.Select(c => c == null ? "" : c.Contains(',') || c.Contains('"') || c.Contains('\n') ? "\"" + c.Replace("\"", "\"\"") + "\"" : c));
        private static string Num(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        public async Task<string> SalesCsvAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct)
        {
            var (f, t) = Range(from, to);
            var (rows, _, _) = await LoadAsync(clientId, f, t, deviceId, ct);
            var sb = new StringBuilder();
            sb.AppendLine(Csv(new[] { "Date", "Device", "Currency", "Document", "Tax", "Documents", "Sales incl tax", "Tax amount" }));
            // one line per day, device, currency, document type and tax rate
            var lines = rows.Where(r => r.Payload != null).SelectMany(r => r.Payload!.ReceiptTaxes.Select(x => (r.Date, r.ZimraId, r.Currency, r.Type, x.TaxPercent, Sales: Signed(r.Type, x.SalesAmountWithTax), Tax: Signed(r.Type, x.TaxAmount), r.Payload)))
                .GroupBy(x => (Date: x.Date.Date, x.ZimraId, x.Currency, x.Type, x.TaxPercent)).OrderBy(g => g.Key.Date).ThenBy(g => g.Key.ZimraId).ThenBy(g => g.Key.Currency).ThenBy(g => g.Key.Type);
            foreach (var g in lines)
                sb.AppendLine(Csv(new[] { g.Key.Date.ToString("yyyy-MM-dd"), g.Key.ZimraId.ToString(), g.Key.Currency, g.Key.Type.ToString(), TaxLabel(g.Key.TaxPercent), g.Select(x => x.Payload).Distinct().Count().ToString(), Num(g.Sum(x => x.Sales)), Num(g.Sum(x => x.Tax)) }));
            return sb.ToString();
        }

        public async Task<IReadOnlyList<FiscalDayReportRowDto>> FiscalDaysAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct)
        {
            var (f, t) = Range(from, to);
            var devices = await DevicesAsync(clientId, deviceId, ct);
            var ids = devices.Select(d => d.Id).ToList();
            var zimra = devices.ToDictionary(d => d.Id, d => d.ZimraDeviceId);
            var end = t.AddDays(1);
            var days = await _db.FiscalDays.AsNoTracking().Include(d => d.Counters).Where(d => ids.Contains(d.DeviceId) && d.OpenedAt >= f && d.OpenedAt < end)
                .OrderByDescending(d => d.OpenedAt).Take(500).ToListAsync(ct);
            return days.Select(d => new FiscalDayReportRowDto(zimra.GetValueOrDefault(d.DeviceId), d.FiscalDayNo, d.Status.ToString(), d.OpenedAt, d.ClosedAt, d.LastReceiptCounter, d.IsOffline,
                d.Counters.GroupBy(c => (c.Currency, c.Type)).OrderBy(g => g.Key.Currency).ThenBy(g => g.Key.Type).Select(g => new CounterTotalDto(g.Key.Currency, g.Key.Type.ToString(), g.Sum(c => c.Value))).ToList())).ToList();
        }

        public async Task<string> FiscalDaysCsvAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct)
        {
            var rows = await FiscalDaysAsync(clientId, from, to, deviceId, ct);
            var sb = new StringBuilder();
            sb.AppendLine(Csv(new[] { "Device", "Fiscal day", "Status", "Opened", "Closed", "Receipts", "Mode", "Currency", "Counter", "Value" }));
            foreach (var r in rows)
                foreach (var c in r.Totals.DefaultIfEmpty(new CounterTotalDto("", "", 0)))
                    sb.AppendLine(Csv(new[] { r.DeviceZimraId.ToString(), r.FiscalDayNo.ToString(), r.Status, r.OpenedAt.ToString("yyyy-MM-dd HH:mm"), r.ClosedAt?.ToString("yyyy-MM-dd HH:mm"), r.Receipts.ToString(), r.Offline ? "Offline" : "Online", c.Currency, c.Type, c.Type.Length == 0 ? "" : Num(c.Value) }));
            return sb.ToString();
        }
    }

    // =================================================================================================================
    /// <summary>
    /// A health check for fiscal compliance: everything that makes ZIMRA reject, block or flag a company, in one list, each with a plain explanation and the page that
    /// fixes it. Built from the local database only (it never calls ZIMRA), limited to the caller's devices.
    /// </summary>
    public sealed class ComplianceService : IComplianceService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IBillingService _billing;
        public ComplianceService(FdmsDbContext db, ICallerScope scope, IBillingService billing) { _db = db; _scope = scope; _billing = billing; }

        public async Task<ComplianceDto> CheckAsync(Guid clientId, CancellationToken ct)
        {
            var checks = new List<ComplianceCheckDto>();
            void Add(string id, string area, string title, string status, string detail, string? device = null, string? route = null) => checks.Add(new ComplianceCheckDto(id, area, title, status, detail, device, route));

            var devices = await _db.Devices.AsNoTracking().InScope(_scope).Where(d => d.ClientId == clientId).OrderBy(d => d.ZimraDeviceId).ToListAsync(ct);
            if (devices.Count == 0) Add("devices", "Devices", "No fiscal device registered yet", "info", "Register your ZIMRA device to start fiscalising: the checks appear here once you have one.", null, "/devices/register");
            var now = DateTime.UtcNow; var cat = FdmsClock.CatNow();
            var ids = devices.Select(d => d.Id).ToList();
            var days = (await _db.FiscalDays.AsNoTracking().Where(d => ids.Contains(d.DeviceId)).OrderByDescending(d => d.FiscalDayNo).ToListAsync(ct)).GroupBy(d => d.DeviceId).ToDictionary(g => g.Key, g => g.First());
            var pending = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId) && r.Status == ReceiptStatus.Pending).GroupBy(r => r.DeviceId)
                .Select(g => new { Device = g.Key, N = g.Count(), Oldest = g.Min(r => r.CreatedAt) }).ToDictionaryAsync(x => x.Device, ct);
            var since = now.AddDays(-30);
            var rejected = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId) && r.Status == ReceiptStatus.Rejected && r.CreatedAt >= since).GroupBy(r => r.DeviceId).Select(g => new { Device = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Device, x => x.N, ct);
            var flagged = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId) && r.ValidationErrorsJson != null && r.ValidationErrorsJson.Contains("Red") && r.Status != ReceiptStatus.Cancelled)
                .GroupBy(r => r.DeviceId).Select(g => new { Device = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Device, x => x.N, ct);
            var files = await _db.OfflineFiles.AsNoTracking().Where(f => ids.Contains(f.DeviceId) && f.Status != OfflineFileStatus.Processed).GroupBy(f => f.DeviceId)
                .Select(g => new { Device = g.Key, N = g.Count(), Bad = g.Count(f => f.Status == OfflineFileStatus.ProcessedWithErrors) }).ToDictionaryAsync(x => x.Device, ct);

            foreach (var d in devices)
            {
                var label = $"Device {d.ZimraDeviceId}" + (string.IsNullOrWhiteSpace(d.BranchName) ? "" : $" ({d.BranchName})");
                if (!d.IsActive) Add($"active:{d.Id}", "Device", "Device is switched off", "warn", "This device is disabled, so nothing can be fiscalised on it.", label, "/devices");

                // certificate
                if (d.CertificateValidTill is not { } till) Add($"cert:{d.Id}", "Certificate", "Certificate expiry unknown", "warn", "Refresh the device configuration to read the certificate's validity.", label, "/devices");
                else
                {
                    var left = (till - now).TotalDays;
                    Add($"cert:{d.Id}", "Certificate", left < 0 ? "Certificate has expired" : "Certificate validity", left < 7 ? "critical" : left < 30 ? "warn" : "ok",
                        left < 0 ? $"It expired on {till:d MMM yyyy}: ZIMRA will refuse this device until it is renewed." : left < 30 ? $"It expires on {till:d MMM yyyy} ({(int)left} days). Renew it in good time." : $"Valid until {till:d MMM yyyy}.", label, "/devices");
                }

                // fiscal day
                if (!days.TryGetValue(d.Id, out var day)) Add($"day:{d.Id}", "Fiscal day", "No fiscal day yet", "info", "The first receipt opens one automatically.", label, "/days");
                else if (day.Status == FiscalDayStatus.FiscalDayCloseFailed)
                    Add($"day:{d.Id}", "Fiscal day", $"Closing fiscal day {day.FiscalDayNo} failed", "critical", $"ZIMRA refused the close ({day.ClosingErrorCode ?? "see the fiscal day"}). Correct the flagged receipts, then close again.", label, "/days");
                else if (day.Status != FiscalDayStatus.FiscalDayClosed)
                {
                    var age = (cat - day.OpenedAt).TotalHours;
                    var status = age >= d.DayMaxHrs ? "critical" : age >= d.DayMaxHrs - 2 ? "warn" : "ok";
                    Add($"day:{d.Id}", "Fiscal day", $"Fiscal day {day.FiscalDayNo} is open", status,
                        status == "ok" ? $"Open for {(int)age} h of the {d.DayMaxHrs} h allowed. Close it once a day." : $"Open for {(int)age} h: the limit is {d.DayMaxHrs} h. Close the fiscal day now.", label, "/days");
                }
                else Add($"day:{d.Id}", "Fiscal day", "Fiscal day closed", "ok", $"Fiscal day {day.FiscalDayNo} is closed.", label, "/days");

                // delivery
                if (pending.TryGetValue(d.Id, out var p))
                {
                    var hrs = (now - p.Oldest).TotalHours;
                    Add($"pending:{d.Id}", "Delivery", $"{p.N} receipt(s) waiting to reach ZIMRA", hrs > 24 ? "critical" : hrs > 1 ? "warn" : "info", $"The oldest has waited {(hrs < 1 ? "under an hour" : $"{(int)hrs} h")}. They are delivered automatically once ZIMRA is reachable.", label, "/receipts");
                }
                else Add($"pending:{d.Id}", "Delivery", "Nothing waiting for ZIMRA", "ok", "Every receipt has reached ZIMRA.", label, "/receipts");

                var rj = rejected.GetValueOrDefault(d.Id);
                Add($"rejected:{d.Id}", "Receipts", rj > 0 ? $"{rj} receipt(s) rejected by ZIMRA (30 days)" : "No rejected receipts (30 days)", rj > 0 ? "critical" : "ok",
                    rj > 0 ? "Open them, fix the cause (see the error code) and re-issue with a credit note and a corrected invoice." : "ZIMRA accepted everything sent in the last 30 days.", label, "/receipts");

                var fl = flagged.GetValueOrDefault(d.Id);
                if (fl > 0) Add($"flagged:{d.Id}", "Receipts", $"{fl} receipt(s) flagged red by ZIMRA", "critical", "A red flag blocks closing the fiscal day. Use Correct on each receipt (credit note plus a corrected replacement).", label, "/receipts");

                if (files.TryGetValue(d.Id, out var of))
                    Add($"offline:{d.Id}", "Offline mode", $"{of.N} offline file(s) not yet processed", of.Bad > 0 ? "critical" : "warn", of.Bad > 0 ? $"{of.Bad} file(s) came back with errors from ZIMRA." : "They are uploaded automatically; check again shortly.", label, "/offline");
            }

            // the account itself
            var access = await _billing.GetAccessAsync(clientId, ct);
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (sub != null)
            {
                var daysLeft = (sub.CurrentPeriodEnd - now).TotalDays;
                if (access.Locked) Add("subscription", "Account", "Subscription needs payment", "critical", "Everything except Billing is closed until the invoice is settled.", null, "/billing");
                else if (sub.Status == "Trial") Add("subscription", "Account", "Free trial (demo devices only)", "info", $"The trial ends on {sub.CurrentPeriodEnd:d MMM yyyy}. Pay for a plan to use live devices.", null, "/billing");
                else Add("subscription", "Account", daysLeft < 7 ? "Subscription ends soon" : "Subscription active", daysLeft < 7 ? "warn" : "ok", $"Paid until {sub.CurrentPeriodEnd:d MMM yyyy}.", null, "/billing");
            }
            if (devices.Any(d => d.Environment == 1) == false && devices.Count > 0)
                Add("live", "Account", access.LiveApproved ? "Only demo devices registered" : "Working in demo", "info", access.LiveApproved ? "Register your live device when you are ready to issue real invoices." : "Real invoices need a live device: send a go-live request under Billing.", null, "/billing");
            var hook = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.WebhookUrl).FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(hook) && devices.Count > 0) Add("webhook", "Integration", "No webhook set", "info", "Without a webhook your system must poll for each receipt's status.", null, "/webhooks");

            var ok = checks.Count(c => c.Status == "ok"); var warn = checks.Count(c => c.Status == "warn"); var crit = checks.Count(c => c.Status == "critical");
            var scored = checks.Count(c => c.Status != "info");
            var score = scored == 0 ? 100 : (int)Math.Round(100.0 * (ok + 0.5 * warn) / scored);
            var order = new Dictionary<string, int> { ["critical"] = 0, ["warn"] = 1, ["info"] = 2, ["ok"] = 3 };
            return new ComplianceDto(score, crit > 0 ? "critical" : warn > 0 ? "warn" : "ok", ok, warn, crit, checks.OrderBy(c => order[c.Status]).ToList(), now);
        }
    }
}
