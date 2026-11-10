using System.Net.Http.Headers;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Integrations.Connectors
{
    /// <summary>
    /// Xero (Accounting API 2.0). The organisation is the Xero tenant, sent with every call. Sales invoices (ACCREC) and sales credit notes (ACCRECCREDIT) are read
    /// changed-since; VAT comes from each line's tax type (its effective rate); the HS code is the item code. A credit note's invoice is the one it is allocated to.
    /// Write-back: a history note with the fiscal details and the fiscal PDF attached (shown online).
    /// The sign-in scopes can be changed with Connectors:Xero:Scopes (Xero is moving to finer scopes).
    /// </summary>
    public sealed class XeroConnector : ConnectorHttp, IAccountingConnector
    {
        private const string TokenUrl = "https://identity.xero.com/connect/token";
        private const string Api = "https://api.xero.com/api.xro/2.0";
        private const string DefaultScopes = "offline_access accounting.transactions accounting.contacts.read accounting.settings.read accounting.attachments";
        private readonly string _scopes;
        public XeroConnector(HttpClient http, IConfiguration cfg) : base(http) => _scopes = cfg["Connectors:Xero:Scopes"] is { Length: > 0 } sc ? sc : DefaultScopes;
        public string Key => IntegrationProducts.Xero;
        protected override string Label => "Xero";

        protected override string ErrorText(string body)
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            var v = r.A("Elements").SelectMany(e => e.A("ValidationErrors")).Select(e => e.S("Message")).FirstOrDefault(m => m != null);
            return v ?? r.S("Detail") ?? r.S("Message") ?? r.S("Title") ?? body;
        }

        public string AuthorizeUrl(ConnectorApp app, string redirectUri, string state) =>
            "https://login.xero.com/identity/connect/authorize?" + Query(("response_type", "code"), ("client_id", app.ClientId), ("redirect_uri", redirectUri), ("scope", _scopes), ("state", state));

        public Task<ConnectorTokens> ExchangeCodeAsync(ConnectorApp app, string redirectUri, string code, IReadOnlyDictionary<string, string?> callback, CancellationToken ct) =>
            TokenAsync(TokenUrl, app, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri }, true, false, ct);

        public Task<ConnectorTokens> RefreshAsync(ConnectorApp app, string refreshToken, CancellationToken ct) =>
            TokenAsync(TokenUrl, app, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, true, true, ct);

        public Task<ConnectorTokens> SignInAsync(ConnectorSession s, CancellationToken ct) => throw new ConnectorException(400, "Xero connects with a sign-in.");

        private static HttpRequestMessage Call(ConnectorSession s, HttpMethod method, string path, HttpContent? content = null)
        {
            var req = Request(method, Api + path, s.AccessToken, content);
            req.Headers.Add("xero-tenant-id", s.OrganizationId ?? throw new ConnectorException(400, "No Xero organisation is chosen."));
            return req;
        }

        public async Task<IReadOnlyList<ConnectorOrganization>> OrganizationsAsync(ConnectorSession s, CancellationToken ct)
        {
            var j = await SendAsync(Request(HttpMethod.Get, "https://api.xero.com/connections", s.AccessToken), ct);
            var list = j.ValueKind == JsonValueKind.Array ? j.EnumerateArray().ToList() : new List<JsonElement>();
            return list.Where(t => (t.S("tenantType") ?? "ORGANISATION") == "ORGANISATION")
                .Select(t => new ConnectorOrganization(t.S("tenantId")!, t.S("tenantName") ?? "Xero organisation", null)).Where(o => o.Id != null).ToList();
        }

        private static (string Collection, string Root, string IdField, string NumberField, string Type) Names(string kind) => kind == "creditnote"
            ? ("CreditNotes", "CreditNotes", "CreditNoteID", "CreditNoteNumber", "ACCRECCREDIT")
            : ("Invoices", "Invoices", "InvoiceID", "InvoiceNumber", "ACCREC");

        private static string Status(string? s) => (s ?? "").ToUpperInvariant() switch
        {
            "DRAFT" or "SUBMITTED" => "draft",
            "VOIDED" or "DELETED" => "void",
            "PAID" => "paid",
            _ => "open",
        };

        public async Task<ConnectorPage> ListAsync(ConnectorSession s, string kind, DateTime? since, int page, CancellationToken ct)
        {
            var n = Names(kind);
            var path = $"/{n.Collection}?where={Uri.EscapeDataString($"Type==\"{n.Type}\"")}&order=UpdatedDateUTC&page={page}" + (kind == "invoice" ? "&pageSize=100&summaryOnly=true" : "");
            var req = Call(s, HttpMethod.Get, path);
            if (since is { } d) req.Headers.IfModifiedSince = new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc));
            var j = await SendAsync(req, ct);
            var items = j.A(n.Root).Select(e => new ConnectorDocumentHead(e.S(n.IdField)!, e.S(n.NumberField) ?? e.S(n.IdField)!, Status(e.S("Status")), e.T("DateString") ?? e.T("Date"),
                e.D("Total"), e.S("CurrencyCode") ?? "", e.S("Contact", "Name"), e.T("UpdatedDateUTC"))).ToList();
            return new ConnectorPage(items, items.Count >= 100);
        }

        public async Task<ConnectorDocument> GetAsync(ConnectorSession s, string kind, string id, CancellationToken ct)
        {
            var n = Names(kind);
            var doc = (await SendAsync(Call(s, HttpMethod.Get, $"/{n.Collection}/{Uri.EscapeDataString(id)}"), ct)).A(n.Root).FirstOrDefault();
            if (doc.ValueKind != JsonValueKind.Object) throw new ConnectorException(404, "Xero no longer has this document.");
            var types = doc.S("LineAmountTypes") ?? "Exclusive";
            var taxes = (await SendAsync(Call(s, HttpMethod.Get, "/TaxRates"), ct)).A("TaxRates")
                .GroupBy(t => t.S("TaxType") ?? "").ToDictionary(g => g.Key, g => (Pct: g.First().DN("EffectiveRate") ?? g.First().D("DisplayTaxRate"), Name: g.First().S("Name") ?? ""));

            var lines = new List<ConnectorLine>();
            foreach (var l in doc.A("LineItems"))
            {
                var amount = l.D("LineAmount");
                if (amount == 0 && l.D("Quantity") == 0) continue;   // a text-only line
                var hasTax = taxes.TryGetValue(l.S("TaxType") ?? "", out var tax);
                var exempt = types == "NoTax" || hasTax && (tax.Name.Contains("exempt", StringComparison.OrdinalIgnoreCase) || (l.S("TaxType") ?? "").Contains("EXEMPT"));
                var gross = types == "Exclusive" ? amount + l.D("TaxAmount") : amount;
                var pct = exempt ? (decimal?)null : hasTax ? tax.Pct : amount != 0 ? decimal.Round(l.D("TaxAmount") / (types == "Exclusive" ? amount : amount - l.D("TaxAmount")) * 100, 2) : 0;
                var qty = l.DN("Quantity") is { } q && q > 0 ? q : 1;
                lines.Add(new ConnectorLine(l.S("Description") ?? l.S("ItemCode") ?? "Item", Hs(l.S("ItemCode")), qty, decimal.Round(gross, 2), pct, exempt));
            }

            ConnectorBuyer? buyer = null;
            if (doc.S("Contact", "ContactID") is { } contactId)
            {
                var c = (await SendAsync(Call(s, HttpMethod.Get, "/Contacts/" + Uri.EscapeDataString(contactId)), ct)).A("Contacts").FirstOrDefault();
                var (tin, vat) = TaxNumbers(c.S("TaxNumber"), c.S("CompanyNumber"));
                buyer = new ConnectorBuyer(c.S("Name") ?? doc.S("Contact", "Name") ?? "Customer", tin, vat, c.S("EmailAddress"), null);
            }
            var reference = kind == "creditnote" ? doc.A("Allocations").Select(a => a.S("Invoice", "InvoiceID")).FirstOrDefault(x => x != null) : null;

            return new ConnectorDocument(id, doc.S(n.NumberField) ?? id, Status(doc.S("Status")), doc.T("DateString") ?? doc.T("Date"), doc.S("CurrencyCode") ?? "", doc.D("Total"),
                lines, buyer, reference, doc.S("Reference"));
        }

        public async Task WriteBackAsync(ConnectorSession s, string kind, string id, ConnectorWriteBack wb, CancellationToken ct)
        {
            var n = Names(kind);
            var note = new Dictionary<string, object> { ["HistoryRecords"] = new[] { new Dictionary<string, string> { ["Details"] = Cut(wb.Text, 450) } } };
            await SendAsync(Call(s, HttpMethod.Put, $"/{n.Collection}/{Uri.EscapeDataString(id)}/History", Body(note)), ct);
            if (wb.Pdf is { Length: > 0 } pdf)
            {
                var file = new ByteArrayContent(pdf); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                await SendAsync(Call(s, HttpMethod.Put, $"/{n.Collection}/{Uri.EscapeDataString(id)}/Attachments/{Uri.EscapeDataString(wb.PdfName)}?IncludeOnline=true", file), ct);
            }
        }
    }
}
