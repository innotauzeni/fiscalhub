using System.Text.Json;
using Fdms.Application.Abstractions;

namespace Fdms.Infrastructure.Integrations.Connectors
{
    /// <summary>
    /// Sage Accounting (Business Cloud Accounting API v3.1). The organisation is the Sage business, sent as X-Business. Sales invoices and sales credit notes are read
    /// updated-since; VAT comes from each line's tax rate; the HS code is the product's item code. A credit note's invoice is its original invoice number.
    /// Write-back: the fiscal details are added to the document's notes and the fiscal PDF is attached.
    /// </summary>
    public sealed class SageConnector : ConnectorHttp, IAccountingConnector
    {
        private const string TokenUrl = "https://oauth.accounting.sage.com/token";
        private const string Api = "https://api.accounting.sage.com/v3.1";
        public SageConnector(HttpClient http) : base(http) { }
        public string Key => IntegrationProducts.Sage;
        protected override string Label => "Sage";

        protected override string ErrorText(string body)
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            var first = r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().FirstOrDefault() : r;
            return first.S("$message") ?? first.S("message") ?? body;
        }

        public string AuthorizeUrl(ConnectorApp app, string redirectUri, string state) =>
            "https://www.sageone.com/oauth2/auth/central?" + Query(("filter", "apiv3.1"), ("response_type", "code"), ("client_id", app.ClientId), ("redirect_uri", redirectUri), ("scope", "full_access"), ("state", state));

        public Task<ConnectorTokens> ExchangeCodeAsync(ConnectorApp app, string redirectUri, string code, IReadOnlyDictionary<string, string?> callback, CancellationToken ct) =>
            TokenAsync(TokenUrl, app, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri }, false, false, ct);

        public Task<ConnectorTokens> RefreshAsync(ConnectorApp app, string refreshToken, CancellationToken ct) =>
            TokenAsync(TokenUrl, app, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, false, true, ct);

        public Task<ConnectorTokens> SignInAsync(ConnectorSession s, CancellationToken ct) => throw new ConnectorException(400, "Sage connects with a sign-in.");

        private static HttpRequestMessage Call(ConnectorSession s, HttpMethod method, string path, HttpContent? content = null)
        {
            var req = Request(method, Api + path, s.AccessToken, content);
            req.Headers.Add("X-Business", s.OrganizationId ?? throw new ConnectorException(400, "No Sage business is chosen."));
            return req;
        }

        private static IEnumerable<JsonElement> Items(JsonElement j) => j.ValueKind == JsonValueKind.Array ? j.EnumerateArray().ToList() : j.A("$items");

        public async Task<IReadOnlyList<ConnectorOrganization>> OrganizationsAsync(ConnectorSession s, CancellationToken ct)
        {
            var j = await SendAsync(Request(HttpMethod.Get, Api + "/businesses", s.AccessToken), ct);
            return Items(j).Where(b => b.S("id") != null).Select(b => new ConnectorOrganization(b.S("id")!, b.S("name") ?? b.S("displayed_as") ?? "Sage business", null)).ToList();
        }

        private static (string Collection, string Root, string NumberField, string LinesField) Names(string kind) => kind == "creditnote"
            ? ("sales_credit_notes", "sales_credit_note", "credit_note_number", "credit_note_lines")
            : ("sales_invoices", "sales_invoice", "invoice_number", "invoice_lines");

        private static string Status(JsonElement e) => (e.S("status", "id") ?? "").ToUpperInvariant() switch
        {
            "DRAFT" or "PRO_FORMA" => "draft",
            "VOID" or "VOIDED" or "DELETED" => "void",
            "PAID" => "paid",
            _ => "open",
        };

        public async Task<ConnectorPage> ListAsync(ConnectorSession s, string kind, DateTime? since, int page, CancellationToken ct)
        {
            var n = Names(kind);
            var path = $"/{n.Collection}?items_per_page=100&page={page}&attributes={n.NumberField},date,total_amount,currency,contact_name,status,updated_at"
                       + (since is { } d ? "&updated_or_created_since=" + Uri.EscapeDataString(d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv)) : "");
            var j = await SendAsync(Call(s, HttpMethod.Get, path), ct);
            var items = Items(j).Select(e => new ConnectorDocumentHead(e.S("id")!, e.S(n.NumberField) ?? e.S("displayed_as") ?? e.S("id")!, Status(e), e.T("date"), e.D("total_amount"),
                e.S("currency", "id") ?? "", e.S("contact_name"), e.T("updated_at"))).ToList();
            return new ConnectorPage(items, j.G("$next").ValueKind == JsonValueKind.String);
        }

        public async Task<ConnectorDocument> GetAsync(ConnectorSession s, string kind, string id, CancellationToken ct)
        {
            var n = Names(kind);
            var doc = await SendAsync(Call(s, HttpMethod.Get, $"/{n.Collection}/{Uri.EscapeDataString(id)}"), ct);
            var rates = Items(await SendAsync(Call(s, HttpMethod.Get, "/tax_rates?items_per_page=200&attributes=percentage,name"), ct))
                .Where(r => r.S("id") != null).GroupBy(r => r.S("id")!).ToDictionary(g => g.Key, g => (Pct: g.First().D("percentage"), Name: g.First().S("name") ?? g.First().S("displayed_as") ?? ""));
            var products = new Dictionary<string, string?>();
            var lines = new List<ConnectorLine>();
            foreach (var l in doc.A(n.LinesField))
            {
                var taxId = l.S("tax_rate", "id") ?? "";
                var known = rates.TryGetValue(taxId, out var rate);
                var exempt = taxId.Contains("EXEMPT", StringComparison.OrdinalIgnoreCase) || known && rate.Name.Contains("exempt", StringComparison.OrdinalIgnoreCase);
                var pct = exempt ? (decimal?)null : known ? rate.Pct : l.DN("tax_rate", "percentage") ?? 0;
                var gross = l.DN("total_amount") ?? l.D("net_amount") + l.D("tax_amount");
                string? hs = null;
                if (l.S("product", "id") is { } pid)
                {
                    if (!products.TryGetValue(pid, out hs))
                    {
                        try { hs = Hs((await SendAsync(Call(s, HttpMethod.Get, "/products/" + Uri.EscapeDataString(pid) + "?attributes=item_code"), ct)).S("item_code")); }
                        catch (ConnectorException ex) when (!ex.Unauthorized && !ex.RateLimited) { hs = null; }
                        products[pid] = hs;
                    }
                }
                var qty = l.DN("quantity") is { } q && q > 0 ? q : 1;
                lines.Add(new ConnectorLine(l.S("description") ?? l.S("product", "displayed_as") ?? "Item", hs, qty, decimal.Round(gross, 2), pct, exempt));
            }

            ConnectorBuyer? buyer = null;
            if (doc.S("contact", "id") is { } cid)
            {
                var c = await SendAsync(Call(s, HttpMethod.Get, "/contacts/" + Uri.EscapeDataString(cid)), ct);
                var (tin, vat) = TaxNumbers(c.S("tax_number"), c.S("registered_number"));
                buyer = new ConnectorBuyer(c.S("name") ?? doc.S("contact_name") ?? "Customer", tin, vat, c.S("email"), c.S("telephone"));
            }

            string? reference = null;
            if (kind == "creditnote" && doc.S("original_invoice_number") is { Length: > 0 } invNo)
            {
                var found = await SendAsync(Call(s, HttpMethod.Get, "/sales_invoices?search=" + Uri.EscapeDataString(invNo) + "&attributes=invoice_number"), ct);
                reference = Items(found).FirstOrDefault(i => string.Equals(i.S("invoice_number"), invNo, StringComparison.OrdinalIgnoreCase)).S("id");
            }

            return new ConnectorDocument(id, doc.S(n.NumberField) ?? id, Status(doc), doc.T("date"), doc.S("currency", "id") ?? "", doc.D("total_amount"), lines, buyer, reference, doc.S("notes"));
        }

        public async Task WriteBackAsync(ConnectorSession s, string kind, string id, ConnectorWriteBack wb, CancellationToken ct)
        {
            var n = Names(kind);
            var cur = await SendAsync(Call(s, HttpMethod.Get, $"/{n.Collection}/{Uri.EscapeDataString(id)}?attributes=notes"), ct);
            var notes = cur.S("notes") ?? "";
            if (!notes.Contains(wb.FiscalNumber))
            {
                var text = Cut(string.IsNullOrWhiteSpace(notes) ? wb.Text : notes.TrimEnd() + "\n" + wb.Text, 2000);
                await SendAsync(Call(s, HttpMethod.Put, $"/{n.Collection}/{Uri.EscapeDataString(id)}", Body(new Dictionary<string, object> { [n.Root] = new Dictionary<string, string> { ["notes"] = text } })), ct);
            }
            if (wb.Pdf is { Length: > 0 } pdf)
            {
                var att = new Dictionary<string, object>
                {
                    ["attachment"] = new Dictionary<string, object>
                    {
                        ["file"] = Convert.ToBase64String(pdf), ["file_name"] = wb.PdfName, ["mime_type"] = "application/pdf", ["file_extension"] = ".pdf",
                        ["description"] = "Fiscal " + wb.FiscalNumber, ["attachment_context_type_id"] = kind == "creditnote" ? "SALES_CREDIT_NOTE" : "SALES_INVOICE", ["attachment_context_id"] = id,
                    },
                };
                await SendAsync(Call(s, HttpMethod.Post, "/attachments", Body(att)), ct);
            }
        }
    }
}
