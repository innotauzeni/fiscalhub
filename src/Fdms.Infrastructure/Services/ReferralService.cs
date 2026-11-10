using System.Collections.Concurrent;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The refer-and-earn program. Every company has a referral code. A company that registers with it (and is approved) is linked to the referrer; when that company pays its subscription
    /// invoice the referrer earns a share of it, and, as the number of paying referrals grows, unlocks levels (free months, bonus coins).
    /// The operator chooses how rewards are given: as coins (the default: credited at once, spent in the coins shop on extra time, extra slots or paying an invoice; never cash) or as money
    /// (paid out by the operator). Nothing is hooked into the payment code: rewards are worked out from the paid invoices whenever a referrer or the operator looks, and every reward, level
    /// and coin credit is created once (unique keys), so looking twice never pays twice.
    /// </summary>
    public sealed class ReferralService : IReferralService
    {
        private const string ProgramKey = "Referral.Program";
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        /// <summary>The payment method name of an invoice paid with coins (such invoices earn no referral reward).</summary>
        public const string CoinsMethod = "Coins";
        public static readonly string[] ShopKinds = { "free_days", "extra_device", "extra_user", "extra_branch", "pay_invoice" };
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Spending = new();

        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IBillingService _billing;
        private readonly Billing.PublicUrls _urls;
        public ReferralService(FdmsDbContext db, ICallerScope scope, IBillingService billing, Billing.PublicUrls urls) { _db = db; _scope = scope; _billing = billing; _urls = urls; }

        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

        public static IReadOnlyList<CoinShopItem> DefaultShop() => new[]
        {
            new CoinShopItem("week", "7 extra days", "Adds 7 days to your subscription.", 100, "free_days", 7),
            new CoinShopItem("month", "1 extra month", "Adds 30 days to your subscription.", 350, "free_days", 30),
            new CoinShopItem("device", "1 more device", "One more fiscal device than your plan allows, for good.", 300, "extra_device", 1),
            new CoinShopItem("user", "1 more user", "One more user than your plan allows, for good.", 150, "extra_user", 1),
            new CoinShopItem("branch", "1 more branch", "One more branch than your plan allows, for good.", 150, "extra_branch", 1),
            new CoinShopItem("invoice", "Pay an invoice with coins", "Pay an open subscription invoice with your coins.", 0, "pay_invoice", 0),
        };

        public static ReferralProgramDto DefaultProgram() => new(true, 10m, 1, "Earn coins for every company you refer: 10% of its first subscription invoice. Spend them on extra time, more devices or users, or to pay your own invoice.",
            new[]
            {
                new ReferralTier("bronze", "Bronze", 1, 0, "Referral Starter badge", 50),
                new ReferralTier("silver", "Silver", 3, 1, "Silver Partner badge and 1 free month", 150),
                new ReferralTier("gold", "Gold", 10, 3, "Gold Partner badge and 3 free months", 500),
            }, "coins", 10, DefaultShop());

        private static ReferralProgramDto Normalise(ReferralProgramDto p) => p with
        {
            RewardType = p.RewardType == "money" ? "money" : "coins",
            CoinsPerUnit = p.CoinsPerUnit is > 0 and <= 1000 ? p.CoinsPerUnit : 10,
            Shop = p.Shop ?? DefaultShop(),
        };

        private async Task<ReferralProgramDto> ProgramAsync(CancellationToken ct)
        {
            var json = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == ProgramKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return DefaultProgram();
            try { return Normalise(JsonSerializer.Deserialize<ReferralProgramDto>(json, J) ?? DefaultProgram()); } catch (JsonException) { return DefaultProgram(); }
        }

        /// <summary>Whether the program is switched on (the registration form asks, so a switched-off program ignores codes).</summary>
        public static async Task<bool> EnabledAsync(FdmsDbContext db, CancellationToken ct)
        {
            var json = await db.SystemSettings.AsNoTracking().Where(s => s.Key == ProgramKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return true;
            try { return JsonSerializer.Deserialize<ReferralProgramDto>(json, J)?.Enabled ?? true; } catch (JsonException) { return true; }
        }

        // ---- codes ---------------------------------------------------------------------------------------------------------

        public static string NewCode(Random rnd) => "REF" + new string(Enumerable.Range(0, 6).Select(_ => CodeAlphabet[rnd.Next(CodeAlphabet.Length)]).ToArray());

        public async Task<string> EnsureCodeAsync(Client c, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(c.ReferralCode)) return c.ReferralCode;
            var rnd = Random.Shared;
            for (var i = 0; i < 20; i++)
            {
                var code = NewCode(rnd);
                if (await _db.Clients.AnyAsync(x => x.ReferralCode == code, ct) || await _db.Partners.AnyAsync(p => p.Code == code, ct)) continue;
                c.ReferralCode = code;
                await _db.SaveChangesAsync(ct);
                return code;
            }
            throw new InvalidOperationException("Could not make a unique referral code.");
        }

        public async Task<Guid?> ReferrerForCodeAsync(string? code, CancellationToken ct)
        {
            var c = (code ?? "").Trim().ToUpperInvariant();
            if (c.Length is < 3 or > 20 || !(await ProgramAsync(ct)).Enabled) return null;
            return await _db.Clients.AsNoTracking().Where(x => x.ReferralCode == c && x.IsActive).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        }

        // ---- rewards, levels and coins -------------------------------------------------------------------------------------

        public static int CoinsFor(decimal amount, int perUnit) => (int)Math.Round(amount * perUnit, MidpointRounding.AwayFromZero);

        /// <summary>Creates the rewards, levels and coin credits a referrer has earned so far. Safe to run any number of times.</summary>
        private async Task SyncAsync(Guid referrerId, ReferralProgramDto program, CancellationToken ct)
        {
            if (!program.Enabled) return;
            var referred = await _db.Clients.AsNoTracking().Where(c => c.ReferredByClientId == referrerId).Select(c => c.Id).ToListAsync(ct);
            if (referred.Count == 0) return;
            // invoices paid with coins are not rewarded: coins never turn into more coins
            var paid = await _db.BillingInvoices.AsNoTracking().Where(i => referred.Contains(i.ClientId) && i.Status == "Paid" && i.PaymentMethod != CoinsMethod).ToListAsync(ct);
            var existing = (await _db.ReferralRewards.AsNoTracking().Where(r => r.ReferrerClientId == referrerId).Select(r => new { r.ReferredClientId, r.InvoiceId }).ToListAsync(ct)).Select(r => (r.ReferredClientId, r.InvoiceId)).ToHashSet();
            var added = false;
            foreach (var g in paid.GroupBy(i => i.ClientId))
                foreach (var inv in g.OrderBy(i => i.PaidAt ?? i.IssuedAt).Take(Math.Max(1, program.InvoicesRewarded)))
                {
                    if (existing.Contains((g.Key, inv.Id)) || inv.Subtotal <= 0) continue;
                    var amount = Math.Round(inv.Subtotal * program.Percent / 100m, 2);
                    var reward = new ReferralReward { ReferrerClientId = referrerId, ReferredClientId = g.Key, InvoiceId = inv.Id, Amount = amount, Currency = inv.Currency, Percent = program.Percent };
                    if (program.RewardType == "coins")
                    {
                        reward.Coins = CoinsFor(amount, program.CoinsPerUnit);
                        reward.Status = "Credited";
                        if (reward.Coins > 0) _db.CoinTransactions.Add(new CoinTransaction { ClientId = referrerId, Amount = reward.Coins, Kind = "referral", Reference = reward.Id.ToString("N"), Description = $"Referral reward: {program.Percent:0.##}% of {inv.Number}" });
                    }
                    _db.ReferralRewards.Add(reward);
                    added = true;
                }
            var paying = paid.Select(i => i.ClientId).Distinct().Count();
            var granted = (await _db.ReferralPerks.AsNoTracking().Where(p => p.ClientId == referrerId).Select(p => p.TierKey).ToListAsync(ct)).ToHashSet();
            foreach (var t in program.Tiers.Where(t => paying >= t.Referrals && !granted.Contains(t.Key)))
            {
                _db.ReferralPerks.Add(new ReferralPerk { ClientId = referrerId, TierKey = t.Key, Label = t.Label, FreeMonths = t.FreeMonths });
                if (t.FreeMonths > 0)
                {
                    var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ClientId == referrerId, ct);
                    if (sub != null && sub.Status != "Cancelled") sub.CurrentPeriodEnd = (sub.CurrentPeriodEnd > DateTime.UtcNow ? sub.CurrentPeriodEnd : DateTime.UtcNow).AddMonths(t.FreeMonths);
                }
                if (t.BonusCoins > 0) _db.CoinTransactions.Add(new CoinTransaction { ClientId = referrerId, Amount = t.BonusCoins, Kind = "level", Reference = t.Key, Description = $"Level bonus: {t.Label}" });
                _db.AuditLogs.Add(new AuditLog { ClientId = referrerId, Action = "referral.perk", Entity = "Client", EntityId = referrerId.ToString(), Detail = $"unlocked {t.Label}: {t.Perk}" });
                added = true;
            }
            if (!added) return;
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); }   // another request created the same reward or level first: that is fine
        }

        public async Task<int> BalanceAsync(Guid clientId, CancellationToken ct) => await _db.CoinTransactions.Where(t => t.ClientId == clientId).SumAsync(t => (int?)t.Amount, ct) ?? 0;

        private static List<AmountDto> Totals(IEnumerable<(string Currency, decimal Amount)> rows) =>
            rows.GroupBy(r => r.Currency).Select(g => new AmountDto(g.Key, g.Sum(x => x.Amount))).Where(a => a.Amount != 0).OrderBy(a => a.Currency).ToList();

        // ---- the company's own view ----------------------------------------------------------------------------------------

        public async Task<ReferralOverviewDto> OverviewAsync(Guid clientId, CancellationToken ct)
        {
            var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Company");
            var program = await ProgramAsync(ct);
            var code = await EnsureCodeAsync(client, ct);
            await SyncAsync(clientId, program, ct);

            var referred = await _db.Clients.AsNoTracking().Where(c => c.ReferredByClientId == clientId).OrderBy(c => c.CreatedAt).ToListAsync(ct);
            var ids = referred.Select(c => c.Id).ToList();
            var payingIds = (await _db.BillingInvoices.AsNoTracking().Where(i => ids.Contains(i.ClientId) && i.Status == "Paid").Select(i => i.ClientId).Distinct().ToListAsync(ct)).ToHashSet();
            var pendingApps = await _db.Applications.AsNoTracking().Where(a => a.ReferrerClientId == clientId && a.Status == "Pending").ToListAsync(ct);
            var names = referred.ToDictionary(c => c.Id, c => c.Name);
            var rewards = await _db.ReferralRewards.AsNoTracking().Where(r => r.ReferrerClientId == clientId).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
            var perks = await _db.ReferralPerks.AsNoTracking().Where(p => p.ClientId == clientId).ToListAsync(ct);
            var companies = pendingApps.Select(a => new ReferredCompanyDto(a.CompanyName, "Waiting for approval", false, a.CreatedAt))
                .Concat(referred.Select(c => new ReferredCompanyDto(c.Name, !c.IsActive ? "Suspended" : payingIds.Contains(c.Id) ? "Paying" : "Registered", payingIds.Contains(c.Id), c.CreatedAt))).OrderByDescending(c => c.Since).ToList();
            var money = rewards.Where(r => r.Coins == 0).ToList();
            var earned = Totals(money.Select(r => (r.Currency, r.Amount)));
            var paid = Totals(money.Where(r => r.Status == "Paid").Select(r => (r.Currency, r.Amount)));
            var owed = Totals(money.Where(r => r.Status == "Earned").Select(r => (r.Currency, r.Amount)));
            var tiers = program.Tiers.OrderBy(t => t.Referrals).Select(t => new TierProgressDto(t.Key, t.Label, t.Referrals, t.FreeMonths, t.Perk, perks.Any(p => p.TierKey == t.Key), perks.FirstOrDefault(p => p.TierKey == t.Key)?.GrantedAt, t.BonusCoins)).ToList();
            var history = await _db.CoinTransactions.AsNoTracking().Where(t => t.ClientId == clientId).OrderByDescending(t => t.CreatedAt).Take(50)
                .Select(t => new CoinEntryDto(t.Id, t.Amount, t.Kind, t.Description, t.CreatedAt)).ToListAsync(ct);
            var balance = await BalanceAsync(clientId, ct);
            var open = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId == clientId && i.Status == "Open").OrderBy(i => i.IssuedAt).ToListAsync(ct);
            return new ReferralOverviewDto(program.Enabled, code, "/register?ref=" + code, program.Percent, program.InvoicesRewarded, program.Description, referred.Count, pendingApps.Count, payingIds.Count, earned, paid, owed, tiers, companies,
                rewards.Select(r => new RewardDto(r.Id, names.GetValueOrDefault(r.ReferredClientId, "A company"), r.Amount, r.Currency, r.Percent, r.Status, r.CreatedAt, r.PaidAt, r.Reference, r.Coins)).ToList(),
                program.RewardType, program.CoinsPerUnit, balance, (program.Shop ?? DefaultShop()).Where(i => i.Enabled).ToList(), history,
                new BonusLimitsDto(client.BonusDevices, client.BonusUsers, client.BonusBranches),
                open.Select(i => new CoinInvoiceDto(i.Id, i.Number, i.Total, i.Currency, CoinsFor(i.Total, program.CoinsPerUnit))).ToList(),
                (await _urls.AppAsync(ct)).TrimEnd('/') + "/register?ref=" + code);
        }

        /// <summary>Spends coins. One spend at a time per company, and the balance is checked again inside, so two taps never spend the same coins twice.</summary>
        public async Task<RedeemResultDto> RedeemAsync(Guid clientId, RedeemCoinsRequest req, CancellationToken ct)
        {
            var program = await ProgramAsync(ct);
            if (!program.Enabled) throw GatewayException.Conflict("PROGRAM_OFF", "The rewards program is switched off at the moment.");
            var item = (program.Shop ?? DefaultShop()).FirstOrDefault(i => i.Key == req.Key && i.Enabled) ?? throw GatewayException.NotFound("Reward");
            var gate = Spending.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var balance = await BalanceAsync(clientId, ct);
                var client = await _db.Clients.FirstAsync(c => c.Id == clientId, ct);
                BillingInvoice? invoice = null;
                var cost = item.Cost;
                if (item.Kind == "pay_invoice")
                {
                    invoice = await _db.BillingInvoices.FirstOrDefaultAsync(i => i.Id == req.InvoiceId && i.ClientId == clientId, ct) ?? throw GatewayException.BadRequest("INVOICE_REQUIRED", "Choose the open invoice to pay.");
                    if (invoice.Status != "Open") throw GatewayException.Conflict("INVOICE_NOT_OPEN", "That invoice is not open.");
                    cost = CoinsFor(invoice.Total, program.CoinsPerUnit);
                }
                if (cost <= 0) throw GatewayException.Conflict("REWARD_INVALID", "This reward has no price set. Ask the operator.");
                if (balance < cost) throw GatewayException.Conflict("NOT_ENOUGH_COINS", $"You need {cost} coins and have {balance}.");

                string message;
                switch (item.Kind)
                {
                    case "free_days":
                        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ClientId == clientId, ct) ?? throw GatewayException.Conflict("NO_SUBSCRIPTION", "You have no subscription to add time to.");
                        if (sub.Status == "Cancelled") throw GatewayException.Conflict("SUBSCRIPTION_CANCELLED", "Your subscription was cancelled.");
                        sub.CurrentPeriodEnd = (sub.CurrentPeriodEnd > DateTime.UtcNow ? sub.CurrentPeriodEnd : DateTime.UtcNow).AddDays(item.Value);
                        if (sub.Status is "PastDue" or "Expired") sub.Status = "Active";
                        message = $"{item.Value} days added. Your subscription now runs until {sub.CurrentPeriodEnd:d MMM yyyy}.";
                        break;
                    case "extra_device": client.BonusDevices += item.Value; message = $"You can now have {item.Value} more device(s) than your plan allows."; break;
                    case "extra_user": client.BonusUsers += item.Value; message = $"You can now have {item.Value} more user(s) than your plan allows."; break;
                    case "extra_branch": client.BonusBranches += item.Value; message = $"You can now have {item.Value} more branch(es) than your plan allows."; break;
                    case "pay_invoice": message = $"Invoice {invoice!.Number} is paid with {cost} coins."; break;
                    default: throw GatewayException.BadRequest("REWARD_INVALID", "Unknown reward.");
                }
                var spend = new CoinTransaction { ClientId = clientId, Amount = -cost, Kind = "redeem", Reference = Guid.NewGuid().ToString("N"), Description = invoice == null ? item.Title : $"Paid invoice {invoice.Number}", CreatedBy = _scope.UserName };
                _db.CoinTransactions.Add(spend);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "coins.redeem", Entity = "Client", EntityId = clientId.ToString(), Detail = $"{cost} coins: {spend.Description}" });
                await _db.SaveChangesAsync(ct);
                if (invoice != null)
                {
                    _db.Payments.Add(new Payment { InvoiceId = invoice.Id, ClientId = clientId, Method = CoinsMethod, Status = "Paid", Amount = invoice.Total, Currency = invoice.Currency, Reference = $"COINS-{spend.Reference![..8].ToUpperInvariant()}", ConfirmedAt = DateTime.UtcNow, Notes = $"{cost} coins" });
                    await _db.SaveChangesAsync(ct);
                    await _billing.ApplyInvoicePaidAsync(invoice.Id, CoinsMethod, $"{cost} coins", ct);
                }
                return new RedeemResultDto(message, balance - cost);
            }
            finally { gate.Release(); }
        }

        // ---- the operator --------------------------------------------------------------------------------------------------

        public async Task<ReferralProgramAdminDto> AdminAsync(string? status, CancellationToken ct)
        {
            var program = await ProgramAsync(ct);
            foreach (var id in await _db.Clients.AsNoTracking().Where(c => c.ReferredByClientId != null).Select(c => c.ReferredByClientId!.Value).Distinct().ToListAsync(ct)) await SyncAsync(id, program, ct);
            var all = await _db.ReferralRewards.AsNoTracking().ToListAsync(ct);
            var names = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var shown = all.Where(r => string.IsNullOrWhiteSpace(status) || r.Status == status).OrderByDescending(r => r.CreatedAt).Take(300)
                .Select(r => new AdminRewardDto(r.Id, names.GetValueOrDefault(r.ReferrerClientId, "?"), names.GetValueOrDefault(r.ReferredClientId, "?"), r.Amount, r.Currency, r.Status, r.CreatedAt, r.PaidAt, r.Reference, r.Coins)).ToList();
            var money = all.Where(r => r.Coins == 0).ToList();
            var coins = await _db.CoinTransactions.AsNoTracking().GroupBy(t => t.ClientId).Select(g => new { g.Key, Balance = g.Sum(t => t.Amount) }).ToListAsync(ct);
            var issued = await _db.CoinTransactions.Where(t => t.Amount > 0).SumAsync(t => (int?)t.Amount, ct) ?? 0;
            var spent = -(await _db.CoinTransactions.Where(t => t.Amount < 0).SumAsync(t => (int?)t.Amount, ct) ?? 0);
            return new ReferralProgramAdminDto(program, all.Select(r => r.ReferrerClientId).Distinct().Count(), await _db.Clients.CountAsync(c => c.ReferredByClientId != null, ct),
                Totals(money.Select(r => (r.Currency, r.Amount))), Totals(money.Where(r => r.Status == "Paid").Select(r => (r.Currency, r.Amount))), Totals(money.Where(r => r.Status == "Earned").Select(r => (r.Currency, r.Amount))), shown,
                issued, spent, coins.Where(c => c.Balance != 0).OrderByDescending(c => c.Balance).Take(100).Select(c => new CoinHolderDto(c.Key, names.GetValueOrDefault(c.Key, "?"), c.Balance)).ToList());
        }

        public async Task<ReferralProgramAdminDto> SaveProgramAsync(SaveReferralProgramRequest req, CancellationToken ct)
        {
            if (req.Percent is < 0 or > 100) throw GatewayException.BadRequest("PERCENT_INVALID", "The reward is between 0 and 100 percent.");
            if (req.InvoicesRewarded is < 1 or > 24) throw GatewayException.BadRequest("INVOICES_INVALID", "Reward between 1 and 24 invoices per referred company.");
            var current = await ProgramAsync(ct);
            var tiers = (req.Tiers ?? DefaultProgram().Tiers).Select(t => t with { Key = new string((t.Key ?? "").ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()), Label = (t.Label ?? "").Trim() }).ToList();
            if (tiers.Any(t => t.Key.Length == 0 || t.Label.Length == 0 || t.Label.Length > 40 || t.Referrals < 1 || t.Referrals > 1000 || t.FreeMonths is < 0 or > 24 || t.BonusCoins is < 0 or > 1_000_000))
                throw GatewayException.BadRequest("TIER_INVALID", "Each level needs a name, a number of referrals (1 or more), 0 to 24 free months and 0 or more bonus coins.");
            if (tiers.Select(t => t.Key).Distinct().Count() != tiers.Count) throw GatewayException.BadRequest("TIER_DUPLICATE", "Two levels have the same name.");
            if ((req.Description?.Length ?? 0) > 500) throw GatewayException.BadRequest("TOO_LONG", "The description can be at most 500 characters.");
            var rewardType = (req.RewardType ?? current.RewardType).Trim().ToLowerInvariant();
            if (rewardType is not ("coins" or "money")) throw GatewayException.BadRequest("REWARD_TYPE_INVALID", "Rewards are given as coins or money.");
            var perUnit = req.CoinsPerUnit ?? current.CoinsPerUnit;
            if (perUnit is < 1 or > 1000) throw GatewayException.BadRequest("RATE_INVALID", "Coins per 1.00 is between 1 and 1000.");
            var shop = (req.Shop ?? current.Shop ?? DefaultShop()).Select(i => i with { Key = new string((i.Key ?? "").ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()), Title = (i.Title ?? "").Trim(), Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description.Trim() }).ToList();
            if (shop.Any(i => i.Key.Length == 0 || i.Title.Length is 0 or > 60 || (i.Description?.Length ?? 0) > 200 || !ShopKinds.Contains(i.Kind) || (i.Kind != "pay_invoice" && (i.Cost < 1 || i.Cost > 1_000_000 || i.Value < 1 || i.Value > 366))))
                throw GatewayException.BadRequest("SHOP_INVALID", "Each reward needs a name, a kind, a price in coins (1 or more) and an amount (1 to 366).");
            if (shop.Select(i => i.Key).Distinct().Count() != shop.Count) throw GatewayException.BadRequest("SHOP_DUPLICATE", "Two rewards have the same name.");
            var program = new ReferralProgramDto(req.Enabled, req.Percent, req.InvoicesRewarded, string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(), tiers, rewardType, perUnit, shop);
            var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == ProgramKey, ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = ProgramKey });
            row.Value = JsonSerializer.Serialize(program, J); row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { Action = "referral.program", Entity = "Setting", EntityId = ProgramKey, Detail = $"{(req.Enabled ? "on" : "off")}, {req.Percent}% of {req.InvoicesRewarded} invoice(s) as {rewardType}, {tiers.Count} level(s), {shop.Count} reward(s)" });
            await _db.SaveChangesAsync(ct);
            return await AdminAsync(null, ct);
        }

        public async Task<AdminRewardDto> PayAsync(Guid rewardId, string? reference, CancellationToken ct)
        {
            var r = await _db.ReferralRewards.FirstOrDefaultAsync(x => x.Id == rewardId, ct) ?? throw GatewayException.NotFound("Reward");
            if (r.Status == "Paid") throw GatewayException.Conflict("ALREADY_PAID", "That reward was already paid out.");
            if (r.Status == "Credited") throw GatewayException.Conflict("PAID_AS_COINS", "That reward was given as coins; there is nothing to pay out.");
            if ((reference?.Length ?? 0) > 100) throw GatewayException.BadRequest("TOO_LONG", "The reference can be at most 100 characters.");
            r.Status = "Paid"; r.PaidAt = DateTime.UtcNow; r.Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(); r.PaidBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = r.ReferrerClientId, Action = "referral.pay", Entity = "ReferralReward", EntityId = r.Id.ToString(), Detail = $"{r.Amount:0.00} {r.Currency}" });
            await _db.SaveChangesAsync(ct);
            var names = await _db.Clients.AsNoTracking().Where(c => c.Id == r.ReferrerClientId || c.Id == r.ReferredClientId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            return new AdminRewardDto(r.Id, names.GetValueOrDefault(r.ReferrerClientId, "?"), names.GetValueOrDefault(r.ReferredClientId, "?"), r.Amount, r.Currency, r.Status, r.CreatedAt, r.PaidAt, r.Reference, r.Coins);
        }

        public async Task<RedeemResultDto> AdjustCoinsAsync(AdjustCoinsRequest req, CancellationToken ct)
        {
            if (req.Amount == 0 || Math.Abs(req.Amount) > 1_000_000) throw GatewayException.BadRequest("AMOUNT_INVALID", "Give or take between 1 and 1,000,000 coins.");
            if ((req.Note?.Length ?? 0) > 200) throw GatewayException.BadRequest("TOO_LONG", "The note can be at most 200 characters.");
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ClientId, ct) ?? throw GatewayException.NotFound("Company");
            var gate = Spending.GetOrAdd(client.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var balance = await BalanceAsync(client.Id, ct);
                if (balance + req.Amount < 0) throw GatewayException.Conflict("NOT_ENOUGH_COINS", $"{client.Name} has only {balance} coins.");
                _db.CoinTransactions.Add(new CoinTransaction { ClientId = client.Id, Amount = req.Amount, Kind = "adjust", Reference = Guid.NewGuid().ToString("N"), Description = string.IsNullOrWhiteSpace(req.Note) ? (req.Amount > 0 ? "Coins from the operator" : "Coins taken by the operator") : req.Note.Trim(), CreatedBy = _scope.UserName });
                _db.AuditLogs.Add(new AuditLog { ClientId = client.Id, Action = "coins.adjust", Entity = "Client", EntityId = client.Id.ToString(), Detail = $"{req.Amount:+#;-#} coins: {req.Note}" });
                await _db.SaveChangesAsync(ct);
                return new RedeemResultDto($"{client.Name} now has {balance + req.Amount} coins.", balance + req.Amount);
            }
            finally { gate.Release(); }
        }
    }
}
