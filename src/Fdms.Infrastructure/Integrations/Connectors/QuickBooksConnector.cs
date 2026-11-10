using System.Net.Http.Headers;
using System.Text.Json;
using Fdms.Application.Abstractions;

namespace Fdms.Infrastructure.Integrations.Connectors
{
    /// <summary>
    /// QuickBooks Online (Intuit Accounting API v3). The company chosen at sign-in (the realm) is the organisation. Invoices and credit memos are read through the
    /// query endpoint; VAT comes from each line's tax code (tax code → tax rates); the HS code is the item's SKU. A credit memo names its invoice in a custom
    /// field called "Invoice" (the invoice number). Write-back: the fiscal details go into the private note and the fiscal PDF is attached.
    /// </summary>
    public sealed class QuickBooksConnector : ConnectorHttp, IAccountingConnector
    {
        private const string TokenUrl = "https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer";
        private const string Minor = "minorversion=70";
        public QuickBooksConnector(HttpClient http) : base(http) { }
        public string Key => IntegrationProducts.QuickBooks;
        protected override string Label => "QuickBooks";

        protected override string ErrorText(string body)
        {
            using var d = JsonDocument.Parse(body);
            var e = d.RootElement.A("Fault", "Error").FirstOrDefault();
            return e.S("Detail") ?? e.S("Message") ?? body;
        }

        public string AuthorizeUrl(ConnectorApp app, string redirectUri, string state) =>
            "https://appcenter.intuit.com/connect/oauth2?" + Query(("client_id", app.ClientId), ("response_type", "code"), ("scope", "com.intuit.quickbooks.accounting"), ("redirect_uri", redirectUri), ("state", state));

        public async Task<ConnectorTokens> ExchangeCodeAsync(ConnectorApp app, string redirectUri, string code, IReadOnlyDictionary<string, string?> callback, CancellationToken ct)
        {
            var t = await TokenAsync(TokenUrl, app, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri }, true, false, ct);
            callback.TryGetValue("realmId", out var realm);
            if (string.IsNullOrWhiteSpace(realm)) throw new ConnectorException(400, "QuickBooks did not say which company was chosen.");
            return t with { OrganizationId = realm };
        }

        public Task<ConnectorTokens> RefreshAsync(ConnectorApp app, string refreshToken, CancellationToken ct) =>
            TokenAsync(TokenUrl, app, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, true, true, ct);

        public Task<ConnectorTokens> SignInAsync(ConnectorSession s, CancellationToken ct) => throw new ConnectorException(400, "QuickBooks connects with a sign-in.");

        private static string Base(ConnectorSession s) =>
            (s.Sandbox ? "https://sandbox-quickbooks.api.intuit.com" : "https://quickbooks.api.intuit.com") + "/v3/company/"
            + Uri.EscapeDataString(s.OrganizationId ?? throw new ConnectorException(400, "No QuickBooks company is chosen."));

        private Task<JsonElement> GetAsync(ConnectorSession s, string path, CancellationToken ct) =>
            SendAsync(Request(HttpMethod.Get, Base(s) + path + (path.Contains('?') ? "&" : "?") + Minor, s.AccessToken), ct);

        private Task<JsonElement> QueryAsync(ConnectorSession s, string sql, CancellationToken ct) => GetAsync(s, "/query?query=" + Uri.EscapeDataString(sql), ct);

        private static string Esc(string v) => v.Replace("\\", "\\\\").Replace("'", "\\'");
        private static string Entity(string kind) => kind == "creditnote" ? "CreditMemo" : "Invoice";

        public async Task<IReadOnlyList<ConnectorOrganization>> OrganizationsAsync(ConnectorSession s, CancellationToken ct)
        {
            var info = await GetAsync(s, "/companyinfo/" + Uri.EscapeDataString(s.OrganizationId!), ct);
            string? currency = null;
            try { currency = (await QueryAsync(s, "select * from Preferences", ct)).A("QueryResponse", "Preferences").FirstOrDefault().S("CurrencyPrefs", "HomeCurrency", "value"); }
            catch (ConnectorException ex) when (!ex.Unauthorized) { }
            return new[] { new ConnectorOrganization(s.OrganizationId!, info.S("CompanyInfo", "CompanyName") ?? "QuickBooks company", currency) };
        }

        private static string Status(JsonElement e)
        {
            var total = e.D("TotalAmt");
            if (total == 0 && (e.S("PrivateNote") ?? "").StartsWith("Voided", StringComparison.OrdinalIgnoreCase)) return "void";
            return e.DN("Balance") == 0 && total != 0 ? "paid" : "open";
        }

        private static ConnectorDocumentHead Head(JsonElement e) =>
            new(e.S("Id")!, e.S("DocNumber") ?? e.S("Id")!, Status(e), e.T("TxnDate"), e.D("TotalAmt"), e.S("CurrencyRef", "value") ?? "", e.S("CustomerRef", "name"), e.T("MetaData", "LastUpdatedTime"));

        public async Task<ConnectorPage> ListAsync(ConnectorSession s, string kind, DateTime? since, int page, CancellationToken ct)
        {
            const int size = 100;
            var entity = Entity(kind);
            var where = since is { } d ? $" where MetaData.LastUpdatedTime >= '{d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv)}'" : "";
            var j = await QueryAsync(s, $"select * from {entity}{where} orderby MetaData.LastUpdatedTime startposition {(page - 1) * size + 1} maxresults {size}", ct);
            var items = j.A("QueryResponse", entity).Select(Head).ToList();
            return new ConnectorPage(items, items.Count == size);
        }

        public async Task<ConnectorDocument> GetAsync(ConnectorSession s, string kind, string id, CancellationToken ct)
        {
            var entity = Entity(kind);
            var doc = (await GetAsync(s, $"/{entity.ToLowerInvariant()}/{Uri.EscapeDataString(id)}", ct)).G(entity);
            var mode = doc.S("GlobalTaxCalculation") ?? "TaxExcluded";

            // tax codes → their rates, and which codes are exempt
            var rates = (await QueryAsync(s, "select * from TaxRate maxresults 1000", ct)).A("QueryResponse", "TaxRate").ToDictionary(r => r.S("Id") ?? "", r => r.D("RateValue"));
            var codes = new Dictionary<string, (decimal Pct, bool Exempt)>();
            foreach (var c in (await QueryAsync(s, "select * from TaxCode maxresults 1000", ct)).A("QueryResponse", "TaxCode"))
            {
                var pct = c.A("SalesTaxRateList", "TaxRateDetail").Sum(r => rates.TryGetValue(r.S("TaxRateRef", "value") ?? "", out var v) ? v : 0m);
                codes[c.S("Id") ?? ""] = (pct, (c.S("Name") ?? "").Contains("exempt", StringComparison.OrdinalIgnoreCase));
            }

            // flatten bundles, then look up the items' SKUs (the HS code)
            var raw = new List<JsonElement>();
            void Add(IEnumerable<JsonElement> lines) { foreach (var l in lines) { if (l.S("DetailType") == "GroupLineDetail") Add(l.A("GroupLineDetail", "Line")); else raw.Add(l); } }
            Add(doc.A("Line"));
            var itemIds = raw.Select(l => l.S("SalesItemLineDetail", "ItemRef", "value")).Where(x => x != null).Distinct().ToList();
            var skus = new Dictionary<string, string?>();
            if (itemIds.Count > 0)
                foreach (var it in (await QueryAsync(s, $"select * from Item where Id in ({string.Join(",", itemIds.Select(x => $"'{Esc(x!)}'"))}) maxresults 1000", ct)).A("QueryResponse", "Item"))
                    skus[it.S("Id") ?? ""] = it.S("Sku");

            var lines = new List<ConnectorLine>();
            var usedRates = new HashSet<decimal>();
            foreach (var l in raw.Where(l => l.S("DetailType") == "SalesItemLineDetail"))
            {
                var d = l.G("SalesItemLineDetail");
                var (pct, exempt) = mode == "NotApplicable" ? (0m, true) : codes.TryGetValue(d.S("TaxCodeRef", "value") ?? "", out var tc) ? tc : (0m, false);
                var net = l.D("Amount");
                var gross = mode == "TaxInclusive" ? d.DN("TaxInclusiveAmt") ?? net * (1 + pct / 100) : net * (1 + pct / 100);
                var qty = d.DN("Qty") is { } q && q > 0 ? q : 1;
                usedRates.Add(exempt ? -1 : pct);
                lines.Add(new ConnectorLine(l.S("Description") ?? d.S("ItemRef", "name") ?? "Item", Hs(skus.GetValueOrDefault(d.S("ItemRef", "value") ?? "")), qty, decimal.Round(gross, 2), exempt ? null : pct, exempt));
            }
            foreach (var l in raw.Where(l => l.S("DetailType") == "DiscountLineDetail"))
            {
                if (usedRates.Count != 1) throw new ConnectorException(422, "A discount over lines with different VAT rates cannot be fiscalised exactly. Put the discount on each line in QuickBooks instead.");
                var r = usedRates.First();
                var amount = l.D("Amount");
                lines.Add(new ConnectorLine("Discount", null, 1, -decimal.Round(r < 0 ? amount : amount * (1 + r / 100), 2), r < 0 ? null : r, r < 0, Discount: true));
            }

            var custom = doc.A("CustomField").ToList();
            string? Custom(string word) => custom.FirstOrDefault(f => (f.S("Name") ?? "").Contains(word, StringComparison.OrdinalIgnoreCase)).S("StringValue");
            var (tin, vat) = TaxNumbers(Custom("TIN"), Custom("VAT"));
            var buyer = new ConnectorBuyer(doc.S("CustomerRef", "name") ?? "Customer", tin, vat, doc.S("BillEmail", "Address"), null);

            string? reference = null;
            if (kind == "creditnote" && Custom("Invoice") is { Length: > 0 } invNo)
                reference = (await QueryAsync(s, $"select Id from Invoice where DocNumber = '{Esc(invNo.Trim())}'", ct)).A("QueryResponse", "Invoice").FirstOrDefault().S("Id");

            var currency = doc.S("CurrencyRef", "value");
            if (string.IsNullOrEmpty(currency))
                currency = (await QueryAsync(s, "select * from Preferences", ct)).A("QueryResponse", "Preferences").FirstOrDefault().S("CurrencyPrefs", "HomeCurrency", "value");

            return new ConnectorDocument(id, doc.S("DocNumber") ?? id, Status(doc), doc.T("TxnDate"), currency ?? "", doc.D("TotalAmt"), lines, buyer, reference, doc.S("CustomerMemo", "value"));
        }

        public async Task WriteBackAsync(ConnectorSession s, string kind, string id, ConnectorWriteBack wb, CancellationToken ct)
        {
            var entity = Entity(kind);
            var cur = (await GetAsync(s, $"/{entity.ToLowerInvariant()}/{Uri.EscapeDataString(id)}", ct)).G(entity);
            var note = cur.S("PrivateNote") ?? "";
            if (!note.Contains(wb.FiscalNumber))
            {
                var text = Cut(string.IsNullOrWhiteSpace(note) ? wb.Text : note.TrimEnd() + "\n" + wb.Text, 4000);
                await SendAsync(Request(HttpMethod.Post, $"{Base(s)}/{entity.ToLowerInvariant()}?{Minor}", s.AccessToken,
                    Body(new Dictionary<string, object?> { ["Id"] = id, ["SyncToken"] = cur.S("SyncToken"), ["sparse"] = true, ["PrivateNote"] = text })), ct);
            }
            if (wb.Pdf is { Length: > 0 } pdf)
            {
                var form = new MultipartFormDataContent();
                var meta = Body(new Dictionary<string, object?>
                {
                    ["AttachableRef"] = new[] { new Dictionary<string, object?> { ["EntityRef"] = new Dictionary<string, string> { ["type"] = entity, ["value"] = id } } },
                    ["FileName"] = wb.PdfName, ["ContentType"] = "application/pdf",
                });
                form.Add(meta, "file_metadata_01", "attachment.json");
                var file = new ByteArrayContent(pdf); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                form.Add(file, "file_content_01", wb.PdfName);
                await SendAsync(Request(HttpMethod.Post, $"{Base(s)}/upload?{Minor}", s.AccessToken, form), ct);
            }
        }
    }
}
