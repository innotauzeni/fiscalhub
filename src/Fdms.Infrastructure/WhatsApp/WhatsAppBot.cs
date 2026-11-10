using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Security;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Integrations;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.WhatsApp
{
    /// <summary>
    /// The FiscalHub WhatsApp assistant for company staff. A guided chat: a menu, then one question at a time with buttons and lists, ending in a fiscalised
    /// invoice whose PDF comes back in the chat. Everything runs as the linked person (their role, their devices, the company's subscription), through the same
    /// services as the console, so the rules are the same.
    /// </summary>
    public sealed class WhatsAppBot
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private static readonly Regex QtyNamePrice = new(@"^\s*(\d+(?:[.,]\d+)?)\s*[xX*×]\s*(.+?)\s*@\s*(\d+(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);
        private static readonly Regex NameQtyPrice = new(@"^\s*(.+?)\s+(\d+(?:[.,]\d+)?)\s+(\d+(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);
        private static readonly Regex Link = new(@"^\s*link\s+(\d{6})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly string[] MenuWords = { "menu", "hi", "hello", "hey", "start", "home", "0", "main" };
        private static readonly string[] CancelWords = { "cancel", "stop", "quit", "exit" };

        private readonly FdmsDbContext _db;
        private readonly IServiceProvider _sp;
        private readonly IWhatsAppApi _api;
        private readonly ILogger<WhatsAppBot> _log;
        public WhatsAppBot(FdmsDbContext db, IServiceProvider sp, IWhatsAppApi api, ILogger<WhatsAppBot> log) { _db = db; _sp = sp; _api = api; _log = log; }

        // ---- what a conversation remembers ----
        public sealed class Line { public string Name { get; set; } = ""; public string? Hs { get; set; } public decimal Qty { get; set; } public decimal Price { get; set; } public decimal? Tax { get; set; } public bool Exempt { get; set; } }
        public sealed class Buyer { public string Name { get; set; } = ""; public string? Tin { get; set; } public string? Vat { get; set; } public string? Email { get; set; } public string? Phone { get; set; } }
        public sealed class Data
        {
            public string? Mode { get; set; }
            public Guid? DeviceId { get; set; }
            public string? Currency { get; set; }
            public Buyer? Customer { get; set; }
            public List<Line> Lines { get; set; } = new();
            public Line? Pending { get; set; }
            public string? Payment { get; set; }
            public string? Ref { get; set; }
            public Guid? ReceiptId { get; set; }
            public string? Reason { get; set; }
        }

        private sealed class Ctx
        {
            public required WhatsAppService Service { get; init; }
            public required WhatsAppConfig Cfg { get; init; }
            public required WhatsAppMessage Msg { get; init; }
            public required WhatsAppSession Session { get; init; }
            public required Data Data { get; set; }
            /// <summary>The company number the chat is on; null = the platform's number.</summary>
            public WhatsAppAccount? Account { get; init; }
            public ApplicationUser User { get; set; } = null!;
            public Guid ClientId { get; set; }
            public string CompanyName { get; set; } = "";
            public HashSet<string> Perms { get; set; } = new();
            public List<Guid>? Allowed { get; set; }
            public string Text => (Msg.Body ?? "").Trim();
            public string? Reply => Msg.ReplyId;
            public bool Can(string p) => Perms.Contains(p);
        }

        // =============================================================================================================
        // entry
        // =============================================================================================================

        public async Task HandleAsync(WhatsAppService service, WhatsAppConfig cfg, WhatsAppMessage msg, WhatsAppAccount? account, CancellationToken ct)
        {
            var line = account?.Id.ToString("N") ?? "";
            var session = await _db.WhatsAppSessions.FirstOrDefaultAsync(s => s.Line == line && s.Phone == msg.Phone, ct);
            if (session == null) { session = new WhatsAppSession { Line = line, Phone = msg.Phone, ClientId = account?.ClientId }; _db.WhatsAppSessions.Add(session); }
            session.LastInboundAt = DateTime.UtcNow;
            var data = TryRead(session.DataJson);
            var c = new Ctx { Service = service, Cfg = cfg, Msg = msg, Session = session, Data = data, Account = account };
            if (msg.WaMessageId != null) { try { await _api.MarkReadAsync(cfg, msg.WaMessageId, ct); } catch (WhatsAppException) { } }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.WhatsAppNumber == msg.Phone, ct);
            // on a company's own number, only that company's staff use the assistant; anyone else is a customer (or a stranger)
            if (account != null && user != null && user.ClientId != account.ClientId) user = null;
            var link = Link.Match(c.Text);
            if (link.Success)
            {
                var linked = await service.CompleteLinkAsync(msg.Phone, link.Groups[1].Value, account?.ClientId, ct);
                if (linked == null) { await Say(c, "That code is not valid or has expired. In FiscalHub open your profile → WhatsApp and get a new code.", ct); await SaveAsync(c, ct); return; }
                user = linked;
                session.State = "menu"; c.Data = new Data();
                await Say(c, $"✅ This WhatsApp number is now linked to {linked.Name} {linked.Surname} ({linked.Email}).", ct);
            }
            // the company's plan must include WhatsApp and SMS for its own number to answer at all
            if (account != null && !(await global::Fdms.Infrastructure.Billing.PlanModuleAccess.ForClientAsync(_db, account.ClientId, ct)).Includes("messaging"))
            {
                await Say(c, "This WhatsApp service is not available right now.", ct); await SaveAsync(c, ct); return;
            }
            if (user == null && account is { CustomersEnabled: true })
            {
                msg.ClientId = account.ClientId;
                try { await CustomerChatAsync(c, account, ct); }
                catch (GatewayException ex) { await Say(c, "⚠️ " + ex.Message, ct); }
                await SaveAsync(c, ct);
                return;
            }
            if (user == null)
            {
                await Say(c, "👋 This is the FiscalHub assistant. This number is not linked to a FiscalHub user yet.\n\nSign in at FiscalHub, open your profile → *WhatsApp*, tap *Link WhatsApp* and send the code it shows (LINK 123456).", ct);
                await SaveAsync(c, ct);
                return;
            }
            msg.UserId = user.Id; msg.ClientId = user.ClientId; session.UserId = user.Id; session.ClientId = user.ClientId;
            if (user.Status != "ACTIVE") { await Say(c, "Your FiscalHub user is not active. Ask your administrator.", ct); await SaveAsync(c, ct); return; }
            if (user.ClientId is not { } clientId) { await Say(c, "The assistant is for company staff. You will still get platform alerts here.", ct); await SaveAsync(c, ct); return; }
            var company = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId, ct);
            if (company is not { IsActive: true }) { await Say(c, "Your company's account is suspended. Contact the platform operator.", ct); await SaveAsync(c, ct); return; }

            if (!(await global::Fdms.Infrastructure.Billing.PlanModuleAccess.ForClientAsync(_db, clientId, ct)).Includes("messaging"))
            {
                await Say(c, "Your company's plan does not include the WhatsApp assistant. An administrator can upgrade under Billing in FiscalHub.", ct); await SaveAsync(c, ct); return;
            }
            c.User = user; c.ClientId = clientId; c.CompanyName = company.Name;
            c.Perms = await PermissionsAsync(user.Id, ct);
            c.Allowed = await AllowedDevicesAsync(user, ct);
            // from here on, everything happens as this person: their name in the audit trail and their device limits
            using (AmbientCaller.Use(new AmbientCallerInfo(user.Id, user.Email, c.Allowed, "whatsapp")))
            {
                try { await RouteAsync(c, link.Success, ct); }
                catch (GatewayException ex) { await Say(c, "⚠️ " + ex.Message, ct); }
                catch (FdmsException ex) { await Say(c, "⚠️ ZIMRA: " + ex.Message, ct); }
            }
            await SaveAsync(c, ct);
        }

        private static Data TryRead(string json) { try { return JsonSerializer.Deserialize<Data>(json, J) ?? new Data(); } catch (JsonException) { return new Data(); } }

        private async Task SaveAsync(Ctx c, CancellationToken ct)
        {
            c.Session.DataJson = JsonSerializer.Serialize(c.Data, J);
            c.Session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        private async Task<HashSet<string>> PermissionsAsync(string userId, CancellationToken ct) =>
            (await (from ur in _db.UserRoles.AsNoTracking() join rp in _db.RolePermissions.AsNoTracking() on ur.RoleId equals rp.RoleId
                    where ur.UserId == userId && rp.Permission.IsActive select rp.Permission.Name).Distinct().ToListAsync(ct)).ToHashSet();

        /// <summary>Null = every device of the company; otherwise only these (the same rule as signing in to the console).</summary>
        private async Task<List<Guid>?> AllowedDevicesAsync(ApplicationUser u, CancellationToken ct)
        {
            if (u.AccessScope != "restricted") return null;
            var branchIds = _db.UserBranches.Where(x => x.UserId == u.Id).Select(x => x.BranchId);
            var direct = _db.UserDevices.Where(x => x.UserId == u.Id).Select(x => x.DeviceId);
            return await _db.Devices.AsNoTracking().Where(d => d.ClientId == u.ClientId && ((d.BranchId != null && branchIds.Contains(d.BranchId.Value)) || direct.Contains(d.Id))).Select(d => d.Id).ToListAsync(ct);
        }

        // ---- sending (each message is also written to the log) ----

        private void Log(Ctx c, string kind, string body) =>
            _db.WhatsAppMessages.Add(new WhatsAppMessage { Direction = "out", Kind = kind, Phone = c.Msg.Phone, UserId = c.Msg.UserId, ClientId = c.Msg.ClientId, Body = body.Length > 2000 ? body[..2000] : body, Status = "Sent", ProcessedAt = DateTime.UtcNow });

        private async Task Say(Ctx c, string text, CancellationToken ct) { await _api.SendTextAsync(c.Cfg, c.Msg.Phone, text, ct); Log(c, "text", text); }
        private async Task Buttons(Ctx c, string text, CancellationToken ct, params (string Id, string Title)[] b)
        {
            await _api.SendButtonsAsync(c.Cfg, c.Msg.Phone, text, b.Select(x => new WhatsAppButton(x.Id, x.Title)).ToList(), ct);
            Log(c, "button", text + " [" + string.Join(" | ", b.Select(x => x.Title)) + "]");
        }
        private async Task List(Ctx c, string text, string button, IReadOnlyList<WhatsAppListRow> rows, CancellationToken ct)
        {
            await _api.SendListAsync(c.Cfg, c.Msg.Phone, text, button, rows, ct);
            Log(c, "list", text + " [" + string.Join(" | ", rows.Select(r => r.Title)) + "]");
        }
        private async Task Document(Ctx c, byte[] pdf, string name, string caption, CancellationToken ct)
        {
            await _api.SendDocumentAsync(c.Cfg, c.Msg.Phone, pdf, "application/pdf", name, caption, ct);
            Log(c, "document", name);
        }

        private static string Money(decimal v) => v.ToString("#,0.00", Inv);
        private static bool Number(string s, out decimal v) => decimal.TryParse(s.Replace(',', '.').Trim(), NumberStyles.Number, Inv, out v);

        // =============================================================================================================
        // routing
        // =============================================================================================================

        private void Reset(Ctx c) { c.Session.State = "menu"; c.Data = new Data(); }

        private async Task RouteAsync(Ctx c, bool justLinked, CancellationToken ct)
        {
            var text = c.Text.ToLowerInvariant();
            var r = c.Reply;
            if (justLinked || MenuWords.Contains(text) || r == "m:menu") { Reset(c); await MenuAsync(c, ct); return; }
            if (CancelWords.Contains(text) || r == "x:cancel") { Reset(c); await Say(c, "Cancelled. Nothing was fiscalised.", ct); await MenuAsync(c, ct); return; }
            if (text == "help" || r == "m:help") { await HelpAsync(c, ct); return; }
            if (c.Msg.Kind == "other") { await Say(c, "I can read text and buttons only. Type *menu* to see what I can do.", ct); return; }

            // a menu choice works from anywhere
            switch (r)
            {
                case "m:new": await StartSaleAsync(c, "invoice", ct); return;
                case "m:quick": await StartSaleAsync(c, "quick", ct); return;
                case "m:today": await TodayAsync(c, ct); return;
                case "m:recent": await RecentAsync(c, ct); return;
                case "m:day": c.Data = new Data { Mode = "day" }; await NeedDeviceAsync(c, ct); return;
                case "m:credit": await CreditListAsync(c, ct); return;
                case "m:approvals": await ApprovalsAsync(c, ct); return;
                case "m:device": c.Data = new Data { Mode = "setdevice" }; await AskDeviceAsync(c, ct); return;
                case "x:restart": var mode = c.Data.Mode ?? "invoice"; await StartSaleAsync(c, mode, ct); return;
            }
            if (r != null && r.StartsWith("d:")) { await DevicePickedAsync(c, Guid.Parse(r[2..]), ct); return; }
            if (r != null && r.StartsWith("r:")) { await ReceiptPickedAsync(c, Guid.Parse(r[2..]), ct); return; }
            if (r != null && r.StartsWith("cn:")) { c.Data = new Data { Mode = "credit", ReceiptId = Guid.Parse(r[3..]) }; c.Session.State = "credit_reason"; await Say(c, "Why is this invoice being credited? Type the reason (it is sent to ZIMRA), e.g. *Goods returned*.", ct); return; }
            if (r != null && r.StartsWith("ap:")) { await ApprovalPickedAsync(c, Guid.Parse(r[3..]), ct); return; }
            if (r != null && (r.StartsWith("apok:") || r.StartsWith("apno:"))) { await DecideAsync(c, Guid.Parse(r[5..]), r.StartsWith("apok:"), ct); return; }
            if (r is "day:open" or "day:close") { await DayActionAsync(c, r == "day:open", ct); return; }

            switch (c.Session.State)
            {
                case "currency": await CurrencyAsync(c, ct); return;
                case "currency_text": await CurrencyTextAsync(c, ct); return;
                case "customer": await CustomerAsync(c, ct); return;
                case "cust_search": await CustomerSearchAsync(c, ct); return;
                case "cust_pick": await CustomerPickedAsync(c, ct); return;
                case "cust_tin": await CustomerTinAsync(c, ct); return;
                case "item": await ItemAsync(c, ct); return;
                case "qty": await QtyAsync(c, ct); return;
                case "line_tax": await LineTaxAsync(c, ct); return;
                case "line_hs": await LineHsAsync(c, ct); return;
                case "lines": await LinesAsync(c, ct); return;
                case "payment": await PaymentAsync(c, ct); return;
                case "confirm": await ConfirmAsync(c, ct); return;
                case "credit_reason": await CreditReasonAsync(c, ct); return;
                case "credit_confirm": await CreditConfirmAsync(c, ct); return;
                default: await MenuAsync(c, ct); return;
            }
        }

        private async Task MenuAsync(Ctx c, CancellationToken ct)
        {
            var rows = new List<WhatsAppListRow>();
            if (c.Can(Perm.ReceiptsSubmit)) { rows.Add(new("m:new", "New invoice", "Customer, items, payment")); rows.Add(new("m:quick", "Quick sale", "Walk-in customer, fast")); }
            if (c.Can(Perm.ReceiptsView)) { rows.Add(new("m:today", "Today's sales", "Totals for today")); rows.Add(new("m:recent", "Recent receipts", "Get a PDF or a share link")); }
            if (c.Can(Perm.DayView)) rows.Add(new("m:day", "Fiscal day", "Status, open or close"));
            if (c.Can(Perm.ReceiptsSubmit)) rows.Add(new("m:credit", "Credit note", "Reverse a recent invoice"));
            if (c.Can(Perm.ApprovalsView)) rows.Add(new("m:approvals", "Approvals", "Requests waiting for you"));
            if ((await DevicesAsync(c, ct)).Count > 1) rows.Add(new("m:device", "Change device", "The till your sales go to"));
            rows.Add(new("m:help", "Help", "What I can do"));
            var hour = FdmsClock.CatNow().Hour;
            var hello = hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";
            await List(c, $"{hello}, {c.User.Name} 👋\n*{c.CompanyName}*\n\nWhat would you like to do?", "Menu", rows, ct);
        }

        private async Task HelpAsync(Ctx c, CancellationToken ct) =>
            await Say(c, "*FiscalHub on WhatsApp*\n\n" +
                         "• *menu*: what I can do\n• *cancel*: stop what you are doing\n\n" +
                         "When adding items, type part of an item's name to pick it from your list, or type it in full:\n  *2 x Bread @ 1.50*  or  *Bread 2 1.50*\nPrices include VAT.\n\n" +
                         "Every invoice is fiscalised with ZIMRA on your device and you get its PDF here.", ct);

        // =============================================================================================================
        // devices
        // =============================================================================================================

        private async Task<List<Device>> DevicesAsync(Ctx c, CancellationToken ct)
        {
            var q = _db.Devices.AsNoTracking().Where(d => d.ClientId == c.ClientId && d.IsActive);
            if (c.Allowed != null) q = q.Where(d => c.Allowed.Contains(d.Id));
            return await q.OrderBy(d => d.Name).ThenBy(d => d.ZimraDeviceId).ToListAsync(ct);
        }

        private static string Label(Device d) => string.IsNullOrWhiteSpace(d.Name) ? $"Device {d.ZimraDeviceId}" : $"{d.Name} ({d.ZimraDeviceId})";

        private async Task AskDeviceAsync(Ctx c, CancellationToken ct)
        {
            var devices = await DevicesAsync(c, ct);
            if (devices.Count == 0) { Reset(c); await Say(c, "You have no fiscal device to use. Ask your administrator.", ct); return; }
            c.Session.State = "device";
            await List(c, "Which device?", "Devices", devices.Take(10).Select(d => new WhatsAppListRow("d:" + d.Id, Label(d), d.Environment == 1 ? "Production" : "Test")).ToList(), ct);
        }

        /// <summary>Uses the person's usual device, the only one, or asks.</summary>
        private async Task<bool> NeedDeviceAsync(Ctx c, CancellationToken ct)
        {
            var devices = await DevicesAsync(c, ct);
            var chosen = devices.FirstOrDefault(d => d.Id == c.Data.DeviceId) ?? devices.FirstOrDefault(d => d.Id == c.User.WhatsAppDeviceId) ?? (devices.Count == 1 ? devices[0] : null);
            if (chosen == null) { await AskDeviceAsync(c, ct); return false; }
            c.Data.DeviceId = chosen.Id;
            await ContinueAfterDeviceAsync(c, ct);
            return true;
        }

        private async Task DevicePickedAsync(Ctx c, Guid id, CancellationToken ct)
        {
            if (!(await DevicesAsync(c, ct)).Any(d => d.Id == id)) { await Say(c, "That device is not available to you.", ct); return; }
            c.Data.DeviceId = id;
            var u = await _db.Users.FirstAsync(x => x.Id == c.User.Id, ct);
            u.WhatsAppDeviceId = id; c.User.WhatsAppDeviceId = id;   // remembered for next time
            if (c.Data.Mode == "setdevice") { Reset(c); await Say(c, "Done. Your sales go to this device from now on.", ct); await MenuAsync(c, ct); return; }
            await ContinueAfterDeviceAsync(c, ct);
        }

        private async Task ContinueAfterDeviceAsync(Ctx c, CancellationToken ct)
        {
            if (c.Data.Mode == "day") { await DayStatusAsync(c, ct); return; }
            c.Session.State = "currency";
            await Buttons(c, "Which currency?", ct, ("cur:USD", "USD"), ("cur:ZWG", "ZWG"), ("cur:other", "Other"));
        }

        // =============================================================================================================
        // a sale
        // =============================================================================================================

        private async Task<bool> MaySellAsync(Ctx c, CancellationToken ct)
        {
            if (!c.Can(Perm.ReceiptsSubmit)) { await Say(c, "Your role cannot issue invoices.", ct); return false; }
            var access = await _sp.GetRequiredService<IBillingService>().GetAccessAsync(c.ClientId, ct);
            if (access.Locked) { await Say(c, "Your company's subscription needs paying before more invoices can be issued. Open Billing in FiscalHub.", ct); return false; }
            return true;
        }

        private async Task StartSaleAsync(Ctx c, string mode, CancellationToken ct)
        {
            if (!await MaySellAsync(c, ct)) return;
            c.Data = new Data { Mode = mode, DeviceId = c.Data.DeviceId };
            await NeedDeviceAsync(c, ct);
        }

        private async Task CurrencyAsync(Ctx c, CancellationToken ct)
        {
            if (c.Reply == "cur:other") { c.Session.State = "currency_text"; await Say(c, "Type the currency code, e.g. ZAR.", ct); return; }
            var code = c.Reply?.StartsWith("cur:") == true ? c.Reply[4..] : c.Text.ToUpperInvariant();
            await SetCurrencyAsync(c, code, ct);
        }

        private Task CurrencyTextAsync(Ctx c, CancellationToken ct) => SetCurrencyAsync(c, c.Text.ToUpperInvariant(), ct);

        private async Task SetCurrencyAsync(Ctx c, string code, CancellationToken ct)
        {
            if (!Regex.IsMatch(code, "^[A-Z]{3}$")) { await Say(c, "A currency is a 3-letter code, e.g. USD.", ct); return; }
            c.Data.Currency = code;
            if (c.Data.Mode == "quick") { await AskItemAsync(c, ct); return; }
            c.Session.State = "customer";
            await Buttons(c, "Who is the customer?", ct, ("c:walkin", "Walk-in customer"), ("c:search", "Find a customer"));
        }

        private async Task CustomerAsync(Ctx c, CancellationToken ct)
        {
            if (c.Reply == "c:walkin") { c.Data.Customer = null; await AskItemAsync(c, ct); return; }
            if (c.Reply == "c:search") { c.Session.State = "cust_search"; await Say(c, "Type the customer's name or TIN.", ct); return; }
            await CustomerSearchAsync(c, ct);   // typed straight away
        }

        private async Task CustomerSearchAsync(Ctx c, CancellationToken ct)
        {
            var s = c.Text;
            if (s.Length < 2) { await Say(c, "Type at least 2 letters of the name, or the TIN.", ct); return; }
            var found = await _db.Customers.AsNoTracking().Where(x => x.ClientId == c.ClientId && x.IsActive && (x.Name.Contains(s) || (x.Tin != null && x.Tin == s) || (x.TradeName != null && x.TradeName.Contains(s))))
                .OrderBy(x => x.Name).Take(9).ToListAsync(ct);
            c.Data.Customer = new Buyer { Name = s.Length > 120 ? s[..120] : s };
            c.Session.State = "cust_pick";
            var rows = found.Select(x => new WhatsAppListRow("c:" + x.Id, x.Name, x.Tin != null ? "TIN " + x.Tin : x.Phone)).ToList();
            rows.Add(new WhatsAppListRow("c:new", "New: " + s, "Use this name for a new customer"));
            await List(c, found.Count == 0 ? $"No saved customer matches \"{s}\"." : "Choose the customer:", "Customers", rows, ct);
        }

        private async Task CustomerPickedAsync(Ctx c, CancellationToken ct)
        {
            if (c.Reply == "c:new") { c.Session.State = "cust_tin"; await Buttons(c, $"TIN of {c.Data.Customer?.Name} (10 digits), or skip if they have none.", ct, ("tin:skip", "Skip")); return; }
            if (c.Reply?.StartsWith("c:") == true && Guid.TryParse(c.Reply[2..], out var id))
            {
                var x = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id && k.ClientId == c.ClientId, ct);
                if (x == null) { await Say(c, "That customer was not found.", ct); return; }
                c.Data.Customer = new Buyer { Name = x.Name, Tin = x.Tin, Vat = x.VatNumber, Email = x.Email, Phone = x.Phone };
                await AskItemAsync(c, ct);
                return;
            }
            await CustomerSearchAsync(c, ct);   // typed another name
        }

        private async Task CustomerTinAsync(Ctx c, CancellationToken ct)
        {
            if (c.Reply == "tin:skip" || c.Text.Equals("skip", StringComparison.OrdinalIgnoreCase)) { if (c.Data.Customer != null) c.Data.Customer.Tin = null; await AskItemAsync(c, ct); return; }
            var tin = Regex.Replace(c.Text, @"\D", "");
            if (tin.Length != 10) { await Buttons(c, "A TIN has 10 digits. Type it again, or skip.", ct, ("tin:skip", "Skip")); return; }
            if (c.Data.Customer != null) c.Data.Customer.Tin = tin;
            await AskItemAsync(c, ct);
        }

        private async Task AskItemAsync(Ctx c, CancellationToken ct)
        {
            c.Session.State = "item";
            await Say(c, c.Data.Lines.Count == 0
                ? $"Add the first item ({c.Data.Currency}, prices include VAT).\n\nType part of its name to pick it from your items, or type it in full:\n*2 x Bread @ 1.50*"
                : "Next item: type part of its name, or in full like *2 x Bread @ 1.50*.", ct);
        }

        private async Task ItemAsync(Ctx c, CancellationToken ct)
        {
            if (c.Reply?.StartsWith("p:") == true && Guid.TryParse(c.Reply[2..], out var pid))
            {
                var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid && x.ClientId == c.ClientId, ct);
                if (p == null) { await Say(c, "That item was not found.", ct); return; }
                c.Data.Pending = new Line { Name = p.Name, Hs = p.HsCode, Price = p.UnitPrice, Tax = p.IsExempt ? null : p.TaxPercent, Exempt = p.IsExempt };
                c.Session.State = "qty";
                await Say(c, $"How many *{p.Name}*? ({Money(p.UnitPrice)} each)", ct);
                return;
            }
            var t = c.Text;
            var m = QtyNamePrice.Match(t);
            string? name = null; decimal qty = 0, price = 0;
            if (m.Success && Number(m.Groups[1].Value, out qty) && Number(m.Groups[3].Value, out price)) name = m.Groups[2].Value.Trim();
            else if ((m = NameQtyPrice.Match(t)).Success && Number(m.Groups[2].Value, out qty) && Number(m.Groups[3].Value, out price)) name = m.Groups[1].Value.Trim();
            if (name != null)
            {
                if (qty <= 0 || price <= 0) { await Say(c, "The quantity and price must be more than zero.", ct); return; }
                // a saved item with the same name brings its HS code and VAT
                var known = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == c.ClientId && x.IsActive && x.Name == name, ct);
                c.Data.Pending = new Line { Name = name.Length > 200 ? name[..200] : name, Qty = qty, Price = price, Hs = known?.HsCode, Tax = known == null ? null : known.IsExempt ? null : known.TaxPercent, Exempt = known?.IsExempt ?? false };
                if (known != null) { await AddPendingAsync(c, ct); return; }
                c.Session.State = "line_tax";
                await Buttons(c, $"VAT on *{name}*?", ct, ("tax:15", "VAT 15%"), ("tax:0", "Zero-rated 0%"), ("tax:ex", "Exempt"));
                return;
            }
            if (t.Length < 2) { await Say(c, "Type at least 2 letters of the item's name.", ct); return; }
            var found = await _db.Products.AsNoTracking().Where(x => x.ClientId == c.ClientId && x.IsActive && (x.Name.Contains(t) || x.Code == t)).OrderBy(x => x.Name).Take(10).ToListAsync(ct);
            if (found.Count == 0) { await Say(c, $"No item matches \"{t}\". Type it in full like *2 x {t} @ 1.50*.", ct); return; }
            await List(c, "Choose the item:", "Items", found.Select(x => new WhatsAppListRow("p:" + x.Id, x.Name, $"{Money(x.UnitPrice)} {x.Currency}{(x.HsCode != null ? " · HS " + x.HsCode : "")}")).ToList(), ct);
        }

        private async Task QtyAsync(Ctx c, CancellationToken ct)
        {
            if (!Number(c.Text, out var qty) || qty <= 0 || qty > 1_000_000) { await Say(c, "Type the quantity as a number, e.g. 2.", ct); return; }
            if (c.Data.Pending == null) { await AskItemAsync(c, ct); return; }
            c.Data.Pending.Qty = qty;
            await AddPendingAsync(c, ct);
        }

        private async Task LineTaxAsync(Ctx c, CancellationToken ct)
        {
            if (c.Data.Pending == null) { await AskItemAsync(c, ct); return; }
            switch (c.Reply)
            {
                case "tax:15": c.Data.Pending.Tax = 15; c.Data.Pending.Exempt = false; break;
                case "tax:0": c.Data.Pending.Tax = 0; c.Data.Pending.Exempt = false; break;
                case "tax:ex": c.Data.Pending.Tax = null; c.Data.Pending.Exempt = true; break;
                default: await Buttons(c, "Choose the VAT:", ct, ("tax:15", "VAT 15%"), ("tax:0", "Zero-rated 0%"), ("tax:ex", "Exempt")); return;
            }
            c.Session.State = "line_hs";
            await Buttons(c, $"HS code for *{c.Data.Pending.Name}* (8 digits), or skip.", ct, ("hs:skip", "Skip"));
        }

        private async Task LineHsAsync(Ctx c, CancellationToken ct)
        {
            if (c.Data.Pending == null) { await AskItemAsync(c, ct); return; }
            if (c.Reply != "hs:skip" && !c.Text.Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                var hs = Regex.Replace(c.Text, @"\D", "");
                if (hs.Length is not (4 or 6 or 8)) { await Buttons(c, "An HS code has 4, 6 or 8 digits. Type it again, or skip.", ct, ("hs:skip", "Skip")); return; }
                c.Data.Pending.Hs = hs;
            }
            await AddPendingAsync(c, ct);
        }

        private async Task AddPendingAsync(Ctx c, CancellationToken ct)
        {
            if (c.Data.Pending == null) return;
            c.Data.Lines.Add(c.Data.Pending); c.Data.Pending = null;
            if (c.Data.Lines.Count >= 40) { await Say(c, "That is the most items one WhatsApp invoice can hold.", ct); await AskPaymentAsync(c, ct); return; }
            c.Session.State = "lines";
            await Buttons(c, LinesText(c.Data) + "\n\nAnything else?", ct, ("l:more", "Add item"), ("l:done", "Done"), ("l:undo", "Remove last"));
        }

        private static string LinesText(Data d)
        {
            var sb = new StringBuilder("*Items*\n");
            foreach (var l in d.Lines) sb.Append($"• {l.Qty.ToString("0.###", Inv)} × {l.Name} @ {Money(l.Price)} = {Money(l.Qty * l.Price)}{(l.Exempt ? " (exempt)" : l.Tax is { } t ? $" ({t.ToString("0.##", Inv)}% VAT)" : "")}\n");
            sb.Append($"Total so far: *{Money(d.Lines.Sum(l => l.Qty * l.Price))} {d.Currency}*");
            return sb.ToString();
        }

        private async Task LinesAsync(Ctx c, CancellationToken ct)
        {
            switch (c.Reply)
            {
                case "l:more": await AskItemAsync(c, ct); return;
                case "l:undo":
                    if (c.Data.Lines.Count > 0) c.Data.Lines.RemoveAt(c.Data.Lines.Count - 1);
                    if (c.Data.Lines.Count == 0) { await AskItemAsync(c, ct); return; }
                    await Buttons(c, LinesText(c.Data), ct, ("l:more", "Add item"), ("l:done", "Done"), ("l:undo", "Remove last"));
                    return;
                case "l:done": await AskPaymentAsync(c, ct); return;
            }
            c.Session.State = "item";   // typed the next item straight away
            await ItemAsync(c, ct);
        }

        private async Task AskPaymentAsync(Ctx c, CancellationToken ct)
        {
            c.Session.State = "payment";
            await List(c, "How was it paid?", "Payment", new List<WhatsAppListRow>
            {
                new("pay:Cash", "Cash"), new("pay:Card", "Card (swipe)"), new("pay:MobileWallet", "EcoCash / mobile money"),
                new("pay:BankTransfer", "Bank transfer"), new("pay:Credit", "On account (credit)", "The customer pays later"), new("pay:Other", "Other"),
            }, ct);
        }

        private SubmitReceiptRequest Request(Ctx c, decimal? paid)
        {
            var d = c.Data;
            var lines = d.Lines.Select(l => l.Exempt ? new ReceiptLineRequest(l.Name, l.Hs, l.Qty, l.Price, IsExempt: true) : new ReceiptLineRequest(l.Name, l.Hs, l.Qty, l.Price, TaxPercent: l.Tax ?? 0)).ToList();
            var buyer = d.Customer is { Tin: { Length: 10 } } b ? new BuyerRequest(b.Name, null, b.Tin!, b.Vat, b.Email, b.Phone) : null;
            var money = Enum.TryParse<MoneyType>(d.Payment, out var mt) ? mt : MoneyType.Cash;
            var no = "WA" + FdmsClock.CatNow().ToString("yyMMddHHmmss", Inv);
            var notes = d.Customer != null && buyer == null ? "Customer: " + d.Customer.Name : null;
            return new SubmitReceiptRequest(d.Ref ?? ("wa-" + Guid.NewGuid().ToString("N")), no, ReceiptType.FiscalInvoice, d.Currency ?? "USD", true, lines,
                paid is { } p ? new[] { new ReceiptPaymentRequest(money, p) } : null, buyer, notes, null, c.User.Email);
        }

        private async Task PaymentAsync(Ctx c, CancellationToken ct)
        {
            var p = c.Reply?.StartsWith("pay:") == true ? c.Reply[4..] : null;
            if (p == null || !Enum.TryParse<MoneyType>(p, out _)) { await AskPaymentAsync(c, ct); return; }
            c.Data.Payment = p;
            c.Data.Ref = "wa-" + Guid.NewGuid().ToString("N");   // the same invoice even if Fiscalise is tapped twice
            var view = await _sp.GetRequiredService<IReceiptService>().PreviewAsync(c.ClientId, c.Data.DeviceId!.Value, Request(c, null), ct);
            var device = await _db.Devices.AsNoTracking().FirstAsync(x => x.Id == c.Data.DeviceId, ct);
            var sb = new StringBuilder("*FISCAL TAX INVOICE* (check before sending to ZIMRA)\n\n");
            sb.Append($"Customer: {c.Data.Customer?.Name ?? "Walk-in customer"}{(c.Data.Customer?.Tin != null ? " · TIN " + c.Data.Customer.Tin : "")}\n\n");
            foreach (var l in c.Data.Lines) sb.Append($"{l.Qty.ToString("0.###", Inv)} × {l.Name} @ {Money(l.Price)} = {Money(l.Qty * l.Price)}\n");
            foreach (var t in view.Taxes) sb.Append($"\nVAT {(t.TaxPercent is { } tp ? tp.ToString("0.##", Inv) + "%" : "exempt")}: {Money(t.TaxAmount)}");
            sb.Append($"\n\n*Total: {Money(view.Total)} {view.Currency}*\nPaid by: {PaymentLabel(p)}\nDevice: {Label(device)}");
            c.Session.State = "confirm";
            await Buttons(c, sb.ToString(), ct, ("ok:go", "Fiscalise"), ("x:restart", "Start again"), ("x:cancel", "Cancel"));
        }

        private static string PaymentLabel(string p) => p switch { "MobileWallet" => "EcoCash / mobile money", "BankTransfer" => "Bank transfer", "Credit" => "On account", _ => p };

        private async Task ConfirmAsync(Ctx c, CancellationToken ct)
        {
            if (c.Reply != "ok:go") { await Buttons(c, "Tap *Fiscalise* to send it to ZIMRA.", ct, ("ok:go", "Fiscalise"), ("x:restart", "Start again"), ("x:cancel", "Cancel")); return; }
            if (!await MaySellAsync(c, ct)) return;
            var receipts = _sp.GetRequiredService<IReceiptService>();
            var view = await receipts.PreviewAsync(c.ClientId, c.Data.DeviceId!.Value, Request(c, null), ct);
            var receipt = await receipts.SubmitAsync(c.ClientId, c.Data.DeviceId.Value, Request(c, view.Total), ct);
            Reset(c);
            if (receipt.Status == "Rejected") { await Say(c, $"⚠️ ZIMRA rejected the invoice: {receipt.ErrorMessage ?? receipt.ErrorCode}. Nothing was charged; fix it in FiscalHub.", ct); return; }
            await SendReceiptAsync(c, receipt, "✅ *Fiscalised*", ct);
            await Buttons(c, "What next?", ct, ("m:new", "New invoice"), ("m:quick", "Quick sale"), ("m:menu", "Menu"));
        }

        private async Task SendReceiptAsync(Ctx c, ReceiptDto r, string headline, CancellationToken ct, bool toCustomer = false)
        {
            var zimra = await _db.Devices.AsNoTracking().Where(d => d.Id == r.DeviceId).Select(d => d.ZimraDeviceId).FirstOrDefaultAsync(ct);
            var code = IntegrationService.VerificationCode(r.QrUrl);
            var title = r.Type == "CreditNote" ? "FISCAL TAX CREDIT NOTE" : r.Type == "DebitNote" ? "FISCAL TAX DEBIT NOTE" : "FISCAL TAX INVOICE";
            var text = $"{headline}\n{title} *{r.InvoiceNo}*\nTotal: *{Money(r.Total)} {r.Currency}*\nDevice {zimra} · fiscal day {r.FiscalDayNo} · receipt {r.ReceiptCounter}" +
                       (code != null ? $"\nVerification code: {code}" : "") + (r.Status == "Pending" ? "\n\nZIMRA has not confirmed it yet; it is sent again automatically." : "");
            try
            {
                var share = await _sp.GetRequiredService<IInvoiceShareService>().CreateLinkAsync(c.ClientId, r.Id, ct);
                text += toCustomer ? $"\n\nView and verify it online:\n{share.Url}" : $"\n\nForward this link to your customer:\n{share.Url}";
            }
            catch (GatewayException) { }
            await Say(c, text, ct);
            try
            {
                var pdf = await _sp.GetRequiredService<IReceiptService>().GetPdfAsync(c.ClientId, r.Id, true, ct);
                await Document(c, pdf, $"{Regex.Replace(r.InvoiceNo, "[^A-Za-z0-9_-]", "_")}.pdf", $"{title} {r.InvoiceNo}", ct);
            }
            catch (Exception ex) when (ex is WhatsAppException or GatewayException) { _log.LogWarning(ex, "The PDF could not be sent on WhatsApp"); await Say(c, "The PDF could not be attached; open the link above instead.", ct); }
        }

        // =============================================================================================================
        // reports and receipts
        // =============================================================================================================

        private IQueryable<Receipt> MyReceipts(Ctx c)
        {
            var q = _db.Receipts.AsNoTracking().Where(r => r.ClientId == c.ClientId && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held);
            return c.Allowed == null ? q : q.Where(r => c.Allowed.Contains(r.DeviceId));
        }

        private async Task TodayAsync(Ctx c, CancellationToken ct)
        {
            if (!c.Can(Perm.ReceiptsView)) { await Say(c, "Your role cannot see receipts.", ct); return; }
            var start = FdmsClock.CatNow().Date;
            var rows = await MyReceipts(c).Where(r => r.ReceiptDate >= start).Select(r => new { r.Currency, r.Total, r.Status }).ToListAsync(ct);
            var sb = new StringBuilder($"*Today, {start:d MMM yyyy}*\n{rows.Count} receipt(s)\n");
            foreach (var g in rows.GroupBy(r => r.Currency).OrderBy(g => g.Key)) sb.Append($"\n*{Money(g.Sum(r => r.Total))} {g.Key}*  ({g.Count()})");
            var pending = rows.Count(r => r.Status == ReceiptStatus.Pending);
            var rejected = rows.Count(r => r.Status == ReceiptStatus.Rejected);
            if (pending > 0) sb.Append($"\n\n⏳ {pending} waiting for ZIMRA");
            if (rejected > 0) sb.Append($"\n⚠️ {rejected} rejected: open FiscalHub to fix");
            Reset(c);
            await Buttons(c, sb.ToString(), ct, ("m:recent", "Recent receipts"), ("m:menu", "Menu"));
        }

        private async Task RecentAsync(Ctx c, CancellationToken ct)
        {
            if (!c.Can(Perm.ReceiptsView)) { await Say(c, "Your role cannot see receipts.", ct); return; }
            var rows = await MyReceipts(c).OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync(ct);
            if (rows.Count == 0) { await Say(c, "No receipts yet.", ct); return; }
            Reset(c);
            await List(c, "Choose a receipt to get its PDF:", "Receipts", rows.Select(r => new WhatsAppListRow("r:" + r.Id, $"{r.InvoiceNo}", $"{Money(r.Total)} {r.Currency} · {r.ReceiptDate:d MMM HH:mm} · {r.Status}")).ToList(), ct);
        }

        private async Task ReceiptPickedAsync(Ctx c, Guid id, CancellationToken ct)
        {
            if (!c.Can(Perm.ReceiptsView) || !await MyReceipts(c).AnyAsync(r => r.Id == id, ct)) { await Say(c, "That receipt is not available to you.", ct); return; }
            var r = await _sp.GetRequiredService<IReceiptService>().GetAsync(c.ClientId, id, ct);
            await SendReceiptAsync(c, r, "🧾", ct);
        }

        // =============================================================================================================
        // fiscal day
        // =============================================================================================================

        private async Task DayStatusAsync(Ctx c, CancellationToken ct)
        {
            if (!c.Can(Perm.DayView)) { Reset(c); await Say(c, "Your role cannot see fiscal days.", ct); return; }
            var day = await _sp.GetRequiredService<IFiscalDayService>().GetStatusAsync(c.ClientId, c.Data.DeviceId!.Value, ct);
            var open = day.Status == "FiscalDayOpened";
            var text = open ? $"Fiscal day *{day.FiscalDayNo}* is *open* since {day.OpenedAt:d MMM HH:mm}, {day.LastReceiptCounter} receipt(s)."
                            : $"Fiscal day {day.FiscalDayNo} is *{day.Status.Replace("FiscalDay", "").ToLowerInvariant()}*.";
            c.Session.State = "day";
            if (!c.Can(Perm.DayManage)) { await Buttons(c, text, ct, ("m:menu", "Menu")); return; }
            if (open) await Buttons(c, text, ct, ("day:close", "Close the day"), ("m:menu", "Menu"));
            else await Buttons(c, text + "\nIt opens by itself with the first sale, or open it now.", ct, ("day:open", "Open a day"), ("m:menu", "Menu"));
        }

        private async Task DayActionAsync(Ctx c, bool open, CancellationToken ct)
        {
            if (!c.Can(Perm.DayManage)) { await Say(c, "Your role cannot open or close fiscal days.", ct); return; }
            if (c.Data.DeviceId is not { } dev) { c.Data.Mode = "day"; await NeedDeviceAsync(c, ct); return; }
            var days = _sp.GetRequiredService<IFiscalDayService>();
            var d = open ? await days.OpenAsync(c.ClientId, dev, ct) : await days.CloseAsync(c.ClientId, dev, ct);
            Reset(c);
            await Buttons(c, open ? $"✅ Fiscal day {d.FiscalDayNo} is open." : $"✅ Fiscal day {d.FiscalDayNo}: {d.Status.Replace("FiscalDay", "")}. ZIMRA confirms the close shortly.", ct, ("m:menu", "Menu"));
        }

        // =============================================================================================================
        // credit notes
        // =============================================================================================================

        private async Task CreditListAsync(Ctx c, CancellationToken ct)
        {
            if (!await MaySellAsync(c, ct)) return;
            var credited = _db.Receipts.Where(x => x.ClientId == c.ClientId && x.Type == ReceiptType.CreditNote && x.Status != ReceiptStatus.Rejected).Select(x => x.OriginalReceiptId);
            var rows = await MyReceipts(c).Where(r => r.Type == ReceiptType.FiscalInvoice && r.Status == ReceiptStatus.Submitted && !credited.Contains(r.Id))
                .OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync(ct);
            if (rows.Count == 0) { await Say(c, "No recent invoice can be credited.", ct); return; }
            Reset(c);
            await List(c, "Which invoice is being credited (in full)?", "Invoices", rows.Select(r => new WhatsAppListRow("cn:" + r.Id, r.InvoiceNo, $"{Money(r.Total)} {r.Currency} · {r.ReceiptDate:d MMM HH:mm}")).ToList(), ct);
        }

        private async Task CreditReasonAsync(Ctx c, CancellationToken ct)
        {
            var reason = c.Text;
            if (reason.Length < 3 || reason.Length > 200) { await Say(c, "Type a short reason (3 to 200 characters).", ct); return; }
            c.Data.Reason = reason;
            var r = await _db.Receipts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.Data.ReceiptId && x.ClientId == c.ClientId, ct);
            if (r == null) { Reset(c); await Say(c, "That invoice was not found.", ct); return; }
            c.Session.State = "credit_confirm";
            await Buttons(c, $"Issue a *FISCAL TAX CREDIT NOTE* for all of {r.InvoiceNo} ({Money(r.Total)} {r.Currency})?\nReason: {reason}", ct, ("cn:go", "Issue credit note"), ("x:cancel", "Cancel"));
        }

        private async Task CreditConfirmAsync(Ctx c, CancellationToken ct)
        {
            if (c.Reply != "cn:go" || c.Data.ReceiptId is not { } id || c.Data.Reason == null) { await Buttons(c, "Tap *Issue credit note* or Cancel.", ct, ("cn:go", "Issue credit note"), ("x:cancel", "Cancel")); return; }
            if (!await MaySellAsync(c, ct)) return;
            var approvals = _sp.GetRequiredService<IApprovalService>();
            var body = new CorrectReceiptRequest(c.Data.Reason, null, false, null);
            var (total, currency, number) = await approvals.ReceiptAsync(c.ClientId, id, ct);
            // the company's approval matrix applies here exactly as in the console
            var held = await approvals.GateAsync(c.ClientId, "credit_note", total, currency, $"Credit note for {number}: {c.Data.Reason} (WhatsApp)", id.ToString(), body, ct);
            Reset(c);
            if (held != null) { await Buttons(c, $"⏳ {held.Message}", ct, ("m:menu", "Menu")); return; }
            var result = await _sp.GetRequiredService<IReceiptService>().CorrectAsync(c.ClientId, id, body, ct);
            await SendReceiptAsync(c, result.CreditNote, "✅ *Credit note issued*", ct);
        }

        // =============================================================================================================
        // approvals
        // =============================================================================================================

        private async Task ApprovalsAsync(Ctx c, CancellationToken ct)
        {
            if (!c.Can(Perm.ApprovalsView)) { await Say(c, "Your role has no approvals.", ct); return; }
            var list = await _sp.GetRequiredService<IApprovalService>().ListAsync(c.ClientId, "mine", "Pending", ct);
            var mine = list.Items.Where(i => i.CanDecide).Take(10).ToList();
            if (mine.Count == 0) { await Say(c, "Nothing is waiting for you. 👍", ct); return; }
            Reset(c);
            await List(c, $"{mine.Count} request(s) wait for you:", "Requests", mine.Select(i => new WhatsAppListRow("ap:" + i.Id, i.ActionLabel, $"{(i.Amount > 0 ? Money(i.Amount) + " " + i.Currency + " · " : "")}{i.RequestedBy}")).ToList(), ct);
        }

        private async Task ApprovalPickedAsync(Ctx c, Guid id, CancellationToken ct)
        {
            var a = await _sp.GetRequiredService<IApprovalService>().GetAsync(c.ClientId, id, ct);
            if (!a.CanDecide) { await Say(c, "This request is not waiting for you.", ct); return; }
            await Buttons(c, $"*{a.ActionLabel}*\n{a.Summary}\n{(a.Amount > 0 ? $"Amount: {Money(a.Amount)} {a.Currency}\n" : "")}Asked by {a.RequestedBy}, {a.CreatedAt:d MMM HH:mm}", ct,
                ("apok:" + id, "Approve"), ("apno:" + id, "Reject"), ("m:menu", "Menu"));
        }

        private async Task DecideAsync(Ctx c, Guid id, bool approve, CancellationToken ct)
        {
            var svc = _sp.GetRequiredService<IApprovalService>();
            var r = approve ? await svc.ApproveAsync(c.ClientId, id, "Approved on WhatsApp", ct) : await svc.RejectAsync(c.ClientId, id, "Rejected on WhatsApp", ct);
            Reset(c);
            await Buttons(c, $"{(approve ? "✅ Approved" : "❌ Rejected")}: {r.ActionLabel}. Status now: {r.Status}.", ct, ("m:approvals", "More approvals"), ("m:menu", "Menu"));
        }

        // =============================================================================================================
        // customers (on a company's own number): their own invoices only, matched by the phone number on the invoice
        // =============================================================================================================

        private async Task CustomerChatAsync(Ctx c, WhatsAppAccount account, CancellationToken ct)
        {
            c.ClientId = account.ClientId;
            var company = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == account.ClientId, ct);
            if (company is not { IsActive: true }) { await Say(c, "This service is not available right now.", ct); return; }
            c.CompanyName = company.Name;
            var r = c.Reply;
            var text = c.Text.ToLowerInvariant();
            if (r == "k:mine") { await CustomerInvoicesAsync(c, ct); return; }
            if (r == "k:find") { c.Session.State = "k_find"; await Say(c, "Type the invoice number, e.g. INV-1001.", ct); return; }
            if (r == "k:talk") { await CustomerTalkAsync(c, ct); return; }
            if (r != null && r.StartsWith("k:r:") && Guid.TryParse(r[4..], out var id)) { await CustomerReceiptAsync(c, id, ct); return; }
            if (c.Session.State == "k_find" && !MenuWords.Contains(text) && c.Msg.Kind == "text") { await CustomerFindAsync(c, ct); return; }
            await CustomerMenuAsync(c, account, ct);
        }

        private async Task CustomerMenuAsync(Ctx c, WhatsAppAccount account, CancellationToken ct)
        {
            c.Session.State = "k_menu";
            var hello = string.IsNullOrWhiteSpace(account.CustomerGreeting) ? $"👋 Welcome to *{c.CompanyName}* on WhatsApp." : account.CustomerGreeting!;
            await Buttons(c, hello + "\n\nI can send you copies of your fiscal invoices. How can I help?", ct, ("k:mine", "My invoices"), ("k:find", "Find an invoice"), ("k:talk", "Talk to us"));
        }

        /// <summary>
        /// The receipts of this company that belong to the person writing: the buyer's phone on the invoice is their WhatsApp number, or the buyer's TIN is that of a
        /// saved customer whose phone is their number. WhatsApp vouches for the number, so nobody can ask for someone else's invoices.
        /// </summary>
        private async Task<List<Receipt>> CustomerReceiptsAsync(Ctx c, int take, CancellationToken ct)
        {
            var phone = c.Msg.Phone;
            var tail = phone.Length > 4 ? phone[^4..] : phone;
            var tins = (await _db.Customers.AsNoTracking().Where(x => x.ClientId == c.ClientId && x.Phone != null && x.Phone.Contains(tail) && x.Tin != null).Select(x => new { x.Phone, x.Tin }).ToListAsync(ct))
                .Where(x => WhatsAppService.NormalizePhone(x.Phone) == phone).Select(x => x.Tin!).Distinct().Take(5).ToList();
            // a cheap first cut in the database (the last digits of the number, or a saved customer's TIN), then the buyer on each fiscal receipt is read exactly
            var q = _db.Receipts.AsNoTracking().Where(x => x.ClientId == c.ClientId && x.PayloadJson != "" &&
                (x.Status == ReceiptStatus.Submitted || x.Status == ReceiptStatus.Pending || x.Status == ReceiptStatus.OfflineIssued));
            var candidates = await q.Where(x => x.PayloadJson.Contains(tail)).OrderByDescending(x => x.CreatedAt).Take(300).ToListAsync(ct);
            foreach (var tin in tins)
                candidates.AddRange(await q.Where(x => x.PayloadJson.Contains(tin)).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct));
            var mine = new List<Receipt>();
            foreach (var x in candidates.DistinctBy(x => x.Id).OrderByDescending(x => x.CreatedAt))
            {
                if (mine.Count >= take) break;
                var (buyerPhone, buyerTin) = BuyerOf(x.PayloadJson);
                if ((buyerPhone != null && WhatsAppService.NormalizePhone(buyerPhone) == phone) || (buyerTin != null && tins.Contains(buyerTin))) mine.Add(x);
            }
            return mine;
        }

        /// <summary>The buyer's phone and TIN on a fiscal receipt as sent to ZIMRA (buyerData.buyerContacts.phoneNo, buyerData.buyerTIN).</summary>
        private static (string? Phone, string? Tin) BuyerOf(string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var buyer = Find(doc.RootElement, "buyerData", 0);
                if (buyer is not { ValueKind: JsonValueKind.Object } b) return (null, null);
                string? Get(JsonElement e, string name) => e.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } p ? p.Value.GetString() : null;
                var contacts = Find(b, "buyerContacts", 0);
                return (contacts is { ValueKind: JsonValueKind.Object } cs ? Get(cs, "phoneNo") : null, Get(b, "buyerTIN"));
            }
            catch (JsonException) { return (null, null); }

            static JsonElement? Find(JsonElement e, string name, int depth)
            {
                if (e.ValueKind != JsonValueKind.Object || depth > 3) return null;
                foreach (var p in e.EnumerateObject())
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
                    if (p.Value.ValueKind == JsonValueKind.Object && Find(p.Value, name, depth + 1) is { } hit) return hit;
                }
                return null;
            }
        }

        private async Task CustomerInvoicesAsync(Ctx c, CancellationToken ct)
        {
            var mine = await CustomerReceiptsAsync(c, 10, ct);
            c.Session.State = "k_menu";
            if (mine.Count == 0)
            {
                await Buttons(c, $"I could not find invoices from {c.CompanyName} for this number. Invoices are matched by the phone number on them; if yours was issued to another number, tap *Talk to us*.", ct,
                    ("k:find", "Find an invoice"), ("k:talk", "Talk to us"));
                return;
            }
            await List(c, "Your recent invoices. Choose one to get its PDF:", "Invoices",
                mine.Select(x => new WhatsAppListRow("k:r:" + x.Id, x.InvoiceNo.Length > 24 ? x.InvoiceNo[..24] : x.InvoiceNo, $"{Money(x.Total)} {x.Currency} · {x.ReceiptDate:d MMM yyyy}")).ToList(), ct);
        }

        private async Task CustomerFindAsync(Ctx c, CancellationToken ct)
        {
            var number = c.Text.Trim();
            c.Session.State = "k_menu";
            if (number.Length is < 1 or > 50) { await Say(c, "Type the invoice number as it is printed on the invoice.", ct); return; }
            var mine = await CustomerReceiptsAsync(c, 200, ct);
            var hit = mine.FirstOrDefault(x => string.Equals(x.InvoiceNo, number, StringComparison.OrdinalIgnoreCase));
            if (hit == null) { await Buttons(c, $"No invoice {number} was issued to this number.", ct, ("k:mine", "My invoices"), ("k:talk", "Talk to us")); return; }
            await CustomerReceiptAsync(c, hit.Id, ct);
        }

        private async Task CustomerReceiptAsync(Ctx c, Guid id, CancellationToken ct)
        {
            if (!(await CustomerReceiptsAsync(c, 200, ct)).Any(x => x.Id == id)) { await Say(c, "That invoice is not available to this number.", ct); return; }
            var r = await _sp.GetRequiredService<IReceiptService>().GetAsync(c.ClientId, id, ct);
            await SendReceiptAsync(c, r, $"🧾 From *{c.CompanyName}*", ct, toCustomer: true);
            await Buttons(c, "Anything else?", ct, ("k:mine", "My invoices"), ("k:talk", "Talk to us"));
        }

        private async Task CustomerTalkAsync(Ctx c, CancellationToken ct)
        {
            c.Session.State = "k_menu";
            var tail = c.Msg.Phone.Length > 9 ? c.Msg.Phone[^9..] : c.Msg.Phone;
            var name = await _db.Customers.AsNoTracking().Where(x => x.ClientId == c.ClientId && x.Phone != null && x.Phone.Contains(tail))
                .Select(x => x.Name).FirstOrDefaultAsync(ct);
            await _sp.GetRequiredService<INotificationService>().RaiseAsync(c.ClientId, "whatsapp", "info", $"wa-talk:{c.Msg.Phone}:{DateTime.UtcNow:yyyyMMddHH}",
                $"A customer asked to talk on WhatsApp: +{c.Msg.Phone}", (name != null ? name + " " : "") + $"(+{c.Msg.Phone}) messaged your WhatsApp number. Reply to them from WhatsApp.", "/customers", ct);
            await Say(c, $"Thank you. {c.CompanyName} has been told, and someone will reply on this number.", ct);
        }
    }
}
