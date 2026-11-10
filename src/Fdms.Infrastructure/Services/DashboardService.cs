using Fdms.Application.Abstractions;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Services
{
    /// <summary>Cheap overview from the local database only (never calls FDMS), limited to the caller's company and, for tokens, device.</summary>
    public sealed class DashboardService : IDashboardService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IConfiguration _cfg;
        private readonly IUsageService _usage;
        public DashboardService(FdmsDbContext db, ICallerScope scope, IConfiguration cfg, IUsageService usage) { _db = db; _scope = scope; _cfg = cfg; _usage = usage; }

        /// <summary>The first-run list: what a new company still has to do, each step ticking itself off from what really exists.</summary>
        private async Task<ChecklistDto> ChecklistAsync(Guid clientId, IReadOnlyList<Device> devices, CancellationToken ct)
        {
            var anyReceipt = await _db.Receipts.AnyAsync(r => r.ClientId == clientId && (r.Status == ReceiptStatus.Submitted || r.Status == ReceiptStatus.Pending || r.Status == ReceiptStatus.OfflineIssued), ct);
            var closedDay = await _db.FiscalDays.AnyAsync(f => f.Status == FiscalDayStatus.FiscalDayClosed && _db.Devices.Any(d => d.Id == f.DeviceId && d.ClientId == clientId), ct);
            var template = await _db.InvoiceTemplates.AnyAsync(t => t.ClientId == clientId, ct);
            var token = await _db.ApiKeys.AnyAsync(k => k.ClientId == clientId && k.RevokedAt == null, ct);
            var client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == clientId, ct);
            var live = devices.Any(d => d.Environment == (int)global::Fdms.Application.Fdms.FdmsEnvironment.Production);
            var steps = new List<ChecklistStepDto>
            {
                new("device", "Register your ZIMRA device", "Enter the device id, activation key and serial number ZIMRA gave you.", devices.Count > 0, "/devices/register", "Register a device"),
                new("branding", "Make your invoices yours", "Add your logo and colour so printed invoices carry your brand.", template, "/admin/templates", "Set up invoice look"),
                new("invoice", "Issue your first invoice", "Create one from the console. It is fiscalised, signed and gets its QR code.", anyReceipt, "/receipts/new", "Create an invoice"),
                new("connect", "Connect your own system (optional)", "Create an API token so your billing or POS system can fiscalise invoices by itself.", token, "/tokens", "Create an API token"),
                new("day", "Close your first fiscal day", "At the end of trading, close the day so ZIMRA receives the Z report. (A forgotten day closes by itself at 23:59.)", closedDay, "/days", "Open Fiscal days"),
                new("alerts", "Choose who gets alerts", "Tell us where to email warnings: a certificate about to expire, a day that would not close.", client.AlertEmails != null || client.AlertKinds != null, "/alerts", "Set up alerts"),
                new("live", "Go live", "When testing is done, request a live device under Billing and register your production device.", live, "/billing", "Request go-live"),
            };
            return new ChecklistDto(steps.Count(s => s.Done), steps.Count, steps);
        }

        public async Task<DashboardDto> GetAsync(Guid clientId, CancellationToken ct)
        {
            var devices = await _db.Devices.AsNoTracking().InScope(_scope).Where(d => d.ClientId == clientId).OrderBy(d => d.ZimraDeviceId).ToListAsync(ct);
            var ids = devices.Select(d => d.Id).ToList();
            var days = (await _db.FiscalDays.AsNoTracking().Where(d => ids.Contains(d.DeviceId)).OrderByDescending(d => d.FiscalDayNo).ToListAsync(ct))
                .GroupBy(d => d.DeviceId).ToDictionary(g => g.Key, g => g.First());

            var today = FdmsClock.CatNow().Date;
            var receipts = _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId));
            var todays = await receipts.Where(r => r.ReceiptDate >= today && (r.Status == ReceiptStatus.Submitted || r.Status == ReceiptStatus.Pending || r.Status == ReceiptStatus.OfflineIssued))
                .Select(r => new { r.Currency, r.Total }).ToListAsync(ct);
            var counts = await receipts.GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var recent = (await receipts.OrderByDescending(r => r.CreatedAt).Take(8).ToListAsync(ct)).Select(r => r.ToDto()).ToList();

            // receipts that are not yet with ZIMRA, per device: queued after an outage, scheduled (held), and issued offline waiting for the day's file
            var waiting = (await receipts.Where(r => r.Status == ReceiptStatus.Pending || r.Status == ReceiptStatus.Held || r.Status == ReceiptStatus.OfflineIssued)
                .GroupBy(r => new { r.DeviceId, r.Status }).Select(g => new { g.Key.DeviceId, g.Key.Status, N = g.Count() }).ToListAsync(ct))
                .ToLookup(x => x.DeviceId);
            int Waiting(Guid id, ReceiptStatus s) => waiting[id].Where(x => x.Status == s).Sum(x => x.N);

            var list = devices.Select(d =>
            {
                days.TryGetValue(d.Id, out var day);
                var open = day != null && day.Status != FiscalDayStatus.FiscalDayClosed;
                return new DeviceSummary(d.Id, d.ZimraDeviceId, d.TaxPayerName, d.BranchName, d.OperatingMode.ToString(), open ? day!.Status.ToString() : "FiscalDayClosed",
                    day?.FiscalDayNo, open ? day!.LastReceiptCounter : 0, d.CertificateValidTill,
                    open ? day!.OpenedAt : null, day?.ClosingErrorCode, d.DayMaxHrs, Waiting(d.Id, ReceiptStatus.Pending), Waiting(d.Id, ReceiptStatus.Held), Waiting(d.Id, ReceiptStatus.OfflineIssued), d.Name);
            }).ToList();

            var alerts = new List<string>();
            foreach (var d in devices)
            {
                if (d.CertificateValidTill is { } till && till < DateTime.UtcNow.AddDays(30))
                    alerts.Add($"Device {d.ZimraDeviceId}: certificate expires {till:yyyy-MM-dd}.");
                if (days.TryGetValue(d.Id, out var day) && day.Status != FiscalDayStatus.FiscalDayClosed && day.OpenedAt.AddHours(d.DayMaxHrs - 2) < FdmsClock.CatNow())
                    alerts.Add($"Device {d.ZimraDeviceId}: fiscal day {day.FiscalDayNo} is close to its {d.DayMaxHrs}-hour limit - close it.");
            }
            if (counts.GetValueOrDefault(ReceiptStatus.Rejected) > 0) alerts.Add($"{counts[ReceiptStatus.Rejected]} receipt(s) were rejected by ZIMRA - see Receipts.");
            if (counts.GetValueOrDefault(ReceiptStatus.Pending) > 0) alerts.Add($"{counts[ReceiptStatus.Pending]} receipt(s) are waiting to reach ZIMRA (delivered automatically).");

            var trend = await TrendAsync(receipts, today, ct);
            return new DashboardDto(devices.Count, list.Count(x => x.DayStatus != "FiscalDayClosed"), todays.Count,
                todays.GroupBy(t => t.Currency).Select(g => new CurrencyTotal(g.Key, g.Sum(x => x.Total), g.Count())).ToList(),
                counts.GetValueOrDefault(ReceiptStatus.Pending), counts.GetValueOrDefault(ReceiptStatus.Held), counts.GetValueOrDefault(ReceiptStatus.OfflineIssued), counts.GetValueOrDefault(ReceiptStatus.Rejected),
                list, recent, alerts, trend, counts.GetValueOrDefault(ReceiptStatus.Submitted),
                (_cfg["FiscalDay:AutoCloseTime"] ?? FiscalDayAutoCloseJob.DefaultTime) is { Length: > 0 } t ? t : null,
                await ChecklistAsync(clientId, devices, ct), await _usage.GetAsync(clientId, ct));
        }

        /// <summary>Receipts per day for the last 7 days (Zimbabwe time), oldest first, zero-filled.</summary>
        private static async Task<List<TrendPoint>> TrendAsync(IQueryable<Receipt> receipts, DateTime today, CancellationToken ct)
        {
            var from = today.AddDays(-6);
            var rows = await receipts.Where(r => r.ReceiptDate >= from && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held)
                .Select(r => new { r.ReceiptDate, r.Status }).ToListAsync(ct);
            return Enumerable.Range(0, 7).Select(i =>
            {
                var day = from.AddDays(i);
                var of = rows.Where(r => r.ReceiptDate.Date == day).ToList();
                return new TrendPoint(day.ToString("yyyy-MM-dd"), of.Count, of.Count(r => r.Status == ReceiptStatus.Rejected));
            }).ToList();
        }

        public async Task<PlatformDashboardDto> GetPlatformAsync(CancellationToken ct)
        {
            var today = FdmsClock.CatNow().Date;
            var clients = await _db.Clients.AsNoTracking().ToListAsync(ct);
            var devices = await _db.Devices.AsNoTracking().Select(d => new { d.Id, d.ClientId, d.ZimraDeviceId, d.CertificateValidTill }).ToListAsync(ct);
            var receipts = _db.Receipts.AsNoTracking();
            var counts = await receipts.GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var todayCount = await receipts.CountAsync(r => r.ReceiptDate >= today && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held, ct);
            var openDays = await _db.FiscalDays.AsNoTracking().CountAsync(d => d.Status != FiscalDayStatus.FiscalDayClosed, ct);
            var weekByDevice = await receipts.Where(r => r.ReceiptDate >= today.AddDays(-6) && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held)
                .GroupBy(r => r.DeviceId).Select(g => new { DeviceId = g.Key, N = g.Count() }).ToListAsync(ct);
            var deviceClient = devices.ToDictionary(d => d.Id, d => d.ClientId);
            var weekByClient = weekByDevice.Where(x => deviceClient.ContainsKey(x.DeviceId)).GroupBy(x => deviceClient[x.DeviceId]).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
            var top = clients.Select(c => new TopClient(c.Id, c.Name, devices.Count(d => d.ClientId == c.Id), weekByClient.GetValueOrDefault(c.Id)))
                .OrderByDescending(c => c.ReceiptsWeek).ThenBy(c => c.Name).Take(5).ToList();
            var activity = (await _db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(8).ToListAsync(ct))
                .Select(a => new AuditEntryDto(a.Id, a.ClientId, a.ClientId == null ? null : clients.FirstOrDefault(c => c.Id == a.ClientId)?.Name, a.UserName, a.Action, a.Detail, a.At)).ToList();

            var alerts = new List<string>();
            var expiring = devices.Count(d => d.CertificateValidTill is { } t && t < DateTime.UtcNow.AddDays(30));
            if (expiring > 0) alerts.Add($"{expiring} device certificate(s) expire within 30 days.");
            if (counts.GetValueOrDefault(ReceiptStatus.Rejected) > 0) alerts.Add($"{counts[ReceiptStatus.Rejected]} receipt(s) were rejected by ZIMRA across all clients.");
            var suspended = clients.Count(c => !c.IsActive);
            if (suspended > 0) alerts.Add($"{suspended} client(s) are suspended.");

            // what the operator should look at first: one line per company and problem, worst first
            var names = clients.ToDictionary(c => c.Id, c => c.Name);
            var attention = new List<AttentionItemDto>();
            void Add(string kind, string sev, Guid cid, string title, string detail, string? route) { if (names.ContainsKey(cid)) attention.Add(new AttentionItemDto(kind, sev, cid, names[cid], title, detail, route)); }
            var now = DateTime.UtcNow;
            foreach (var g in devices.Where(d => d.CertificateValidTill is { } t && t < now.AddDays(30)).GroupBy(d => d.ClientId))
                Add("certificate", g.Any(d => d.CertificateValidTill < now) ? "bad" : "warn", g.Key, g.Any(d => d.CertificateValidTill < now) ? "Certificate expired" : "Certificate expiring", $"Device(s) {string.Join(", ", g.Select(d => d.ZimraDeviceId))} within 30 days.", "admin/clients");
            var devClient = devices.ToDictionary(d => d.Id, d => d.ClientId);
            foreach (var g in (await receipts.Where(r => r.Status == ReceiptStatus.Rejected && r.CreatedAt > now.AddDays(-3)).GroupBy(r => r.DeviceId).Select(g => new { DeviceId = g.Key, N = g.Count() }).ToListAsync(ct))
                         .Where(x => devClient.ContainsKey(x.DeviceId)).GroupBy(x => devClient[x.DeviceId]))
                Add("rejected", "bad", g.Key, "Receipts rejected by ZIMRA", $"{g.Sum(x => x.N)} in the last 3 days.", "admin/clients");
            foreach (var g in (await receipts.Where(r => r.Status == ReceiptStatus.Pending && r.CreatedAt < now.AddMinutes(-60)).GroupBy(r => r.DeviceId).Select(g => new { DeviceId = g.Key, N = g.Count() }).ToListAsync(ct))
                         .Where(x => devClient.ContainsKey(x.DeviceId)).GroupBy(x => devClient[x.DeviceId]))
                Add("queued", "warn", g.Key, "Receipts stuck waiting for ZIMRA", $"{g.Sum(x => x.N)} waiting for over an hour.", "admin/clients");
            foreach (var g in (await _db.FiscalDays.AsNoTracking().Where(f => f.Status == FiscalDayStatus.FiscalDayCloseFailed).Select(f => new { f.DeviceId, f.FiscalDayNo }).ToListAsync(ct))
                         .Where(x => devClient.ContainsKey(x.DeviceId)).GroupBy(x => devClient[x.DeviceId]))
                Add("dayclose", "bad", g.Key, "Fiscal day will not close", $"{g.Count()} day(s) refused by ZIMRA.", "admin/clients");
            foreach (var s in await _db.Subscriptions.AsNoTracking().Where(s => s.Status != "Cancelled").ToListAsync(ct))
            {
                if (s.Status is "PastDue" or "Suspended") Add("billing", "bad", s.ClientId, s.Status == "Suspended" ? "Suspended for non-payment" : "Payment overdue", "Their invoice is unpaid.", "admin/subscriptions");
                else if (s.Status == "Trial" && s.TrialEndsAt is { } te && te < now.AddDays(3)) Add("trial", "info", s.ClientId, te < now ? "Trial has ended" : "Trial ends soon", $"Trial end: {te:d MMM yyyy}.", "admin/subscriptions");
            }
            foreach (var c in clients.Where(c => !c.IsActive)) Add("suspended", "warn", c.Id, "Company is suspended", "They cannot sign in or issue invoices.", "admin/clients");
            var order = new[] { "bad", "warn", "info" };
            var attentionSorted = attention.OrderBy(a => Array.IndexOf(order, a.Severity)).ThenBy(a => a.ClientName).Take(40).ToList();

            return new PlatformDashboardDto(clients.Count, clients.Count(c => c.IsActive), await _db.Users.AsNoTracking().CountAsync(ct), devices.Count, openDays, todayCount,
                counts.GetValueOrDefault(ReceiptStatus.Rejected), counts.GetValueOrDefault(ReceiptStatus.Pending), await TrendAsync(receipts, today, ct), top, activity, alerts, attentionSorted);
        }
    }
}
