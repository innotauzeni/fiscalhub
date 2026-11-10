using System.Security.Cryptography;
using System.Text;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The platform's own books, kept up to date from billing: each invoice issued to a company (income by kind, VAT, the company owing), its payment (money into cash,
    /// bank, EcoCash or card), the partner commission it earns, and each payout to a partner. Every posting carries a fixed source id, so nothing is posted twice.
    /// </summary>
    public sealed partial class AccountingService
    {
        /// <summary>The platform's own accounts, created the first time they are needed: role, name, type, sub-type.</summary>
        private static readonly (string Role, string Name, string Type, string SubType)[] PlatformAccounts =
        {
            ("plat_subscriptions", "Subscription income", "Income", "revenue"),
            ("plat_integrations", "Integration fees (Zoho, QuickBooks, Xero, Sage, Odoo)", "Income", "revenue"),
            ("plat_golive", "Go-live and set-up fees", "Income", "revenue"),
            ("plat_addons", "Add-on income (devices, users, branches)", "Income", "revenue"),
            ("plat_other_income", "Other platform income", "Income", "revenue"),
            ("plat_commission", "Partner commissions", "Expense", "cost_of_sales"),
            ("plat_coins", "Referral rewards (coins redeemed)", "Expense", "operating_expense"),
            ("plat_commission_due", "Partner commissions payable", "Liability", "current_liability"),
        };

        /// <summary>A fixed id for a posting derived from a billing record ("paid:" + invoice id …), so a second sync finds it posted.</summary>
        private static Guid Derived(string prefix, Guid id) => new(MD5.HashData(Encoding.UTF8.GetBytes(prefix + ":" + id.ToString("N"))));

        private static string IncomeRole(string kind) => kind switch
        {
            "subscription" => "plat_subscriptions", "integration" => "plat_integrations", "golive" => "plat_golive", "addon" => "plat_addons", _ => "plat_other_income",
        };

        /// <summary>Where the money went, from the payment method written on the invoice.</summary>
        private static string MoneyRole(string? method)
        {
            var m = (method ?? "").ToLowerInvariant();
            if (m.Contains("coin")) return "plat_coins";
            if (m.Contains("ecocash") || m.Contains("paynow") || m.Contains("mobile") || m.Contains("onemoney") || m.Contains("innbucks")) return "mobile";
            if (m.Contains("card") || m.Contains("stripe") || m.Contains("paypal")) return "card";
            if (m.Contains("bank") || m.Contains("transfer") || m.Contains("rtgs")) return "bank";
            if (m.Contains("cash")) return "cash";
            return "other";
        }

        private async Task<Dictionary<string, Guid>> PlatformRolesAsync(CancellationToken ct)
        {
            var clientId = PlatformBooks.ClientId;
            var accounts = await _db.LedgerAccounts.Where(a => a.ClientId == clientId && a.Role != null && a.BankAccountId == null).ToListAsync(ct);
            var codes = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).Select(a => a.Code).ToListAsync(ct);
            var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
            var added = false;
            foreach (var (role, name, type, sub) in PlatformAccounts)
            {
                if (accounts.Any(a => a.Role == role)) continue;
                var code = ChartCodes.Next(type, null, codes, rules).Code ?? (type switch { "Income" => "4900", "Expense" => "5800", _ => "2900" }) + accounts.Count;
                codes.Add(code);
                var a = new LedgerAccount { ClientId = clientId, Code = code, Name = name, Type = type, Role = role, SubType = sub, IsSystem = true };
                _db.LedgerAccounts.Add(a); accounts.Add(a); added = true;
            }
            if (added) await _db.SaveChangesAsync(ct);
            return accounts.GroupBy(a => a.Role!).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Code).First().Id);
        }

        /// <summary>Posts what billing has done since the last time. Called with every look at the platform's books.</summary>
        private async Task SyncPlatformAsync(CancellationToken ct)
        {
            var clientId = PlatformBooks.ClientId;
            var role = await PlatformRolesAsync(ct);
            var locked = await LockedUntilAsync(clientId, ct);
            DateTime Open(DateTime d) => locked != null && d.Date <= locked.Value.Date ? locked.Value.Date.AddDays(1) : d.Date;
            var posted = (await _db.JournalEntries.AsNoTracking().Where(e => e.ClientId == clientId && e.SourceId != null).Select(e => new { e.SourceId, e.Id, e.ReversedById }).ToListAsync(ct))
                .ToDictionary(e => e.SourceId!.Value, e => (e.Id, Reversed: e.ReversedById != null));
            var invoices = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId != clientId).OrderBy(i => i.IssuedAt).ToListAsync(ct);
            var companies = await _db.Clients.AsNoTracking().Select(c => new { c.Id, c.Name, c.PartnerId, c.PartnerPercent }).ToDictionaryAsync(c => c.Id, ct);
            var entries = new List<JournalEntry>();
            var seq = await NextSeqAsync(clientId, ct);

            JournalEntry Entry(DateTime date, string currency, string memo, string? reference, string source, Guid sourceId, params (Guid Account, decimal Debit, decimal Credit)[] lines)
            {
                // a record dated in a closed period is posted on the first open day, and says so
                if (Open(date) != date.Date) memo += $" (dated {date:yyyy-MM-dd})";
                var e = NewEntry(clientId, seq++, Open(date), currency, memo.Length > 200 ? memo[..200] : memo, reference?.Length > 60 ? reference[..60] : reference, source,
                    lines.Where(l => l.Debit > 0 || l.Credit > 0).Select(l => (l.Account, l.Debit, l.Credit, (string?)null)));
                e.SourceId = sourceId; e.CreatedBy = "system";
                return e;
            }

            var toReverse = new List<Guid>();
            foreach (var inv in invoices)
            {
                var company = companies.GetValueOrDefault(inv.ClientId)?.Name ?? "a company";
                var issuedKey = inv.Id;
                if (!posted.ContainsKey(issuedKey))
                {
                    if (inv.Status == "Void") continue;   // voided before it was ever in the books
                    var income = inv.Total - inv.TaxAmount;
                    entries.Add(Entry(inv.IssuedAt, inv.Currency, $"Invoice {inv.Number} to {company}: {inv.Description}", inv.Number, "billing", issuedKey,
                        (role["receivable"], inv.Total, 0), (role[IncomeRole(inv.Kind)], 0, income), (role["vat_output"], 0, inv.TaxAmount)));
                    posted[issuedKey] = (Guid.Empty, false);
                }
                else if (inv.Status == "Void" && posted[issuedKey] is { Reversed: false, Id: var entryId } && entryId != Guid.Empty && !posted.ContainsKey(Derived("paid", inv.Id)))
                    toReverse.Add(entryId);   // voided after it was posted: taken back out

                if (inv.Status != "Paid") continue;
                var paidKey = Derived("paid", inv.Id);
                var paidOn = inv.PaidAt ?? inv.IssuedAt;
                if (!posted.ContainsKey(paidKey))
                {
                    var money = role.GetValueOrDefault(MoneyRole(inv.PaymentMethod), role["other"]);
                    entries.Add(Entry(paidOn, inv.Currency, $"Payment of {inv.Number} by {company} ({inv.PaymentMethod ?? "payment"})", inv.PaymentReference ?? inv.Number, "billing_paid", paidKey,
                        (money, inv.Total, 0), (role["receivable"], 0, inv.Total)));
                }
                // the partner who brought the company earns a share of what it paid (as the partner pages work it out)
                var commissionKey = Derived("commission", inv.Id);
                if (!posted.ContainsKey(commissionKey) && companies.GetValueOrDefault(inv.ClientId) is { PartnerId: not null, PartnerPercent: > 0 } c)
                {
                    var commission = Math.Round(inv.Subtotal * c.PartnerPercent / 100m, 2);
                    if (commission > 0)
                        entries.Add(Entry(paidOn, inv.Currency, $"Partner commission on {inv.Number} ({c.PartnerPercent:0.##}%)", inv.Number, "commission", commissionKey,
                            (role["plat_commission"], commission, 0), (role["plat_commission_due"], 0, commission)));
                }
            }

            var partners = await _db.Partners.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            foreach (var p in await _db.PartnerPayouts.AsNoTracking().OrderBy(x => x.Date).ToListAsync(ct))
            {
                if (posted.ContainsKey(p.Id) || p.Amount <= 0) continue;
                entries.Add(Entry(p.Date, p.Currency, $"Commission paid to {partners.GetValueOrDefault(p.PartnerId, "a partner")}", p.Reference, "payout", p.Id,
                    (role["plat_commission_due"], p.Amount, 0), (role["bank"], 0, p.Amount)));
            }

            if (entries.Count > 0)
            {
                _db.JournalEntries.AddRange(entries);
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateException)   // another request posted the same records at the same moment: theirs stand
                {
                    foreach (var en in _db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToList()) en.State = EntityState.Detached;
                    return;
                }
            }
            foreach (var id in toReverse)
                try { await ReverseAtAsync(clientId, id, null, ct); } catch (GatewayException) { /* a closed period: it waits for the next sync */ }
        }
    }
}
