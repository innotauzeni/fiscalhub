using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Integrations.Connectors
{
    /// <summary>
    /// Odoo (Odoo Online, Odoo.sh or the company's own server, version 14 or later) over JSON-RPC with the company's own address, database, user and API key.
    /// The organisation is the Odoo company. Customer invoices (out_invoice) and credit notes (out_refund) are read changed-since; VAT comes from each line's
    /// taxes; the HS code is the product's internal reference. A credit note's invoice is the entry it reverses.
    /// Write-back: a note in the document's chatter and the fiscal PDF as an attachment.
    /// The address must be public: a private or local address is refused (so a company cannot point the gateway at its own network).
    /// </summary>
    public sealed class OdooConnector : ConnectorHttp, IAccountingConnector
    {
        private readonly bool _allowPrivate;
        public OdooConnector(HttpClient http, IConfiguration cfg) : base(http) => _allowPrivate = cfg.GetValue("Connectors:Odoo:AllowPrivateAddresses", false);
        public string Key => IntegrationProducts.Odoo;
        protected override string Label => "Odoo";

        public string AuthorizeUrl(ConnectorApp app, string redirectUri, string state) => throw new ConnectorException(400, "Odoo connects with an API key.");
        public Task<ConnectorTokens> ExchangeCodeAsync(ConnectorApp app, string redirectUri, string code, IReadOnlyDictionary<string, string?> callback, CancellationToken ct) => throw new ConnectorException(400, "Odoo connects with an API key.");
        public Task<ConnectorTokens> RefreshAsync(ConnectorApp app, string refreshToken, CancellationToken ct) => throw new ConnectorException(400, "Odoo connects with an API key.");

        // ---- JSON-RPC ----

        public static bool IsPublic(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168
                         || b[0] == 100 && b[1] >= 64 && b[1] <= 127 || b[0] >= 224);
            }
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC);
        }

        private async Task<Uri> EndpointAsync(ConnectorSession s, CancellationToken ct)
        {
            if (!Uri.TryCreate((s.BaseUrl ?? "").TrimEnd('/') + "/jsonrpc", UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                throw new ConnectorException(400, "The Odoo address must start with https://.");
            if (!_allowPrivate)
            {
                IPAddress[] ips;
                try { ips = IPAddress.TryParse(u.Host, out var lit) ? new[] { lit } : await Dns.GetHostAddressesAsync(u.Host, ct); }
                catch (SocketException) { throw new ConnectorException(400, $"The address {u.Host} could not be found."); }
                if (ips.Length == 0 || ips.Any(ip => !IsPublic(ip))) throw new ConnectorException(400, $"The address {u.Host} is not a public internet address.");
            }
            return u;
        }

        private async Task<JsonElement> RpcAsync(ConnectorSession s, string service, string method, object[] args, CancellationToken ct)
        {
            var body = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["method"] = "call", ["id"] = Random.Shared.Next(1, int.MaxValue),
                ["params"] = new Dictionary<string, object> { ["service"] = service, ["method"] = method, ["args"] = args } };
            var j = await SendAsync(Request(HttpMethod.Post, (await EndpointAsync(s, ct)).ToString(), null, Body(body)), ct);
            if (j.G("error").ValueKind == JsonValueKind.Object)
            {
                var msg = j.S("error", "data", "message") ?? j.S("error", "message") ?? "error";
                var name = j.S("error", "data", "name") ?? "";
                var denied = name.Contains("AccessDenied") || msg.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) || msg.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase);
                throw new ConnectorException(denied ? 401 : 400, "Odoo: " + Cut(msg.Split('\n')[0], 300), unauthorized: denied);
            }
            return j.G("result");
        }

        private int Uid(ConnectorSession s) => int.TryParse(s.UserId, NumberStyles.Integer, Inv, out var uid) ? uid : throw new ConnectorException(401, "Connect Odoo again.", unauthorized: true);

        private Task<JsonElement> ExecAsync(ConnectorSession s, string model, string method, object[] args, Dictionary<string, object>? kwargs, CancellationToken ct) =>
            RpcAsync(s, "object", "execute_kw", new object[] { s.Database ?? "", Uid(s), s.AccessToken, model, method, args, kwargs ?? new Dictionary<string, object>() }, ct);

        private static IEnumerable<JsonElement> Rows(JsonElement j) => j.ValueKind == JsonValueKind.Array ? j.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();

        /// <summary>A many2one value: [id, "name"], or false.</summary>
        private static (int? Id, string? Name) M2O(JsonElement e, string field)
        {
            var v = e.G(field);
            if (v.ValueKind != JsonValueKind.Array) return (null, null);
            var a = v.EnumerateArray().ToList();
            return (a.Count > 0 && a[0].TryGetInt32(out var id) ? id : null, a.Count > 1 ? a[1].GetString() : null);
        }

        private static List<int> Ids(JsonElement e, string field) =>
            e.G(field).ValueKind == JsonValueKind.Array ? e.G(field).EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : new List<int>();

        /// <summary>Odoo sends false for an empty text field.</summary>
        private static string? Text(JsonElement e, string field) => e.G(field).ValueKind == JsonValueKind.String ? e.G(field).GetString() : null;

        // ---- connecting ----

        public async Task<ConnectorTokens> SignInAsync(ConnectorSession s, CancellationToken ct)
        {
            var r = await RpcAsync(s, "common", "authenticate", new object[] { s.Database ?? "", s.Username ?? "", s.AccessToken, new Dictionary<string, object>() }, ct);
            if (r.ValueKind != JsonValueKind.Number || !r.TryGetInt32(out var uid)) throw new ConnectorException(401, "The database, user or API key is wrong.", unauthorized: true);
            return new ConnectorTokens(uid.ToString(Inv), null, 0);
        }

        public async Task<IReadOnlyList<ConnectorOrganization>> OrganizationsAsync(ConnectorSession s, CancellationToken ct)
        {
            var r = await ExecAsync(s, "res.company", "search_read", new object[] { Array.Empty<object>() }, new() { ["fields"] = new[] { "name", "currency_id" } }, ct);
            return Rows(r).Select(c => new ConnectorOrganization(c.S("id")!, Text(c, "name") ?? "Odoo company", M2O(c, "currency_id").Name)).ToList();
        }

        // ---- documents ----

        private static string MoveType(string kind) => kind == "creditnote" ? "out_refund" : "out_invoice";

        private static string Status(JsonElement e) => Text(e, "state") switch
        {
            "draft" => "draft",
            "cancel" => "void",
            _ => Text(e, "payment_state") is "paid" or "in_payment" ? "paid" : "open",
        };

        private static readonly string[] HeadFields = { "name", "state", "payment_state", "invoice_date", "amount_total", "currency_id", "partner_id", "write_date" };

        private static ConnectorDocumentHead Head(JsonElement e) =>
            new(e.S("id")!, Text(e, "name") is { Length: > 0 } nm && nm != "/" ? nm : e.S("id")!, Status(e), e.T("invoice_date"), e.D("amount_total"), M2O(e, "currency_id").Name ?? "", M2O(e, "partner_id").Name, e.T("write_date"));

        public async Task<ConnectorPage> ListAsync(ConnectorSession s, string kind, DateTime? since, int page, CancellationToken ct)
        {
            const int size = 100;
            var domain = new List<object> { new object[] { "move_type", "=", MoveType(kind) } };
            if (int.TryParse(s.OrganizationId, NumberStyles.Integer, Inv, out var company)) domain.Add(new object[] { "company_id", "=", company });
            if (since is { } d) domain.Add(new object[] { "write_date", ">=", d.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", Inv) });
            var r = await ExecAsync(s, "account.move", "search_read", new object[] { domain.ToArray() },
                new() { ["fields"] = HeadFields, ["offset"] = (page - 1) * size, ["limit"] = size, ["order"] = "write_date asc, id asc" }, ct);
            var items = Rows(r).Select(Head).ToList();
            return new ConnectorPage(items, items.Count == size);
        }

        private async Task<List<JsonElement>> ReadAsync(ConnectorSession s, string model, IEnumerable<int> ids, string[] fields, CancellationToken ct)
        {
            var list = ids.Distinct().ToArray();
            if (list.Length == 0) return new List<JsonElement>();
            return Rows(await ExecAsync(s, model, "read", new object[] { list }, new() { ["fields"] = fields }, ct)).ToList();
        }

        public async Task<ConnectorDocument> GetAsync(ConnectorSession s, string kind, string id, CancellationToken ct)
        {
            if (!int.TryParse(id, NumberStyles.Integer, Inv, out var moveId)) throw new ConnectorException(404, "Odoo no longer has this document.");
            var doc = (await ReadAsync(s, "account.move", new[] { moveId }, HeadFields.Concat(new[] { "invoice_line_ids", "reversed_entry_id", "ref" }).ToArray(), ct)).FirstOrDefault();
            if (doc.ValueKind != JsonValueKind.Object) throw new ConnectorException(404, "Odoo no longer has this document.");

            var raw = await ReadAsync(s, "account.move.line", Ids(doc, "invoice_line_ids"), new[] { "name", "quantity", "price_total", "tax_ids", "product_id", "display_type" }, ct);
            raw = raw.Where(l => Text(l, "display_type") is null or "product").ToList();
            var taxes = (await ReadAsync(s, "account.tax", raw.SelectMany(l => Ids(l, "tax_ids")), new[] { "name", "amount", "amount_type" }, ct))
                .ToDictionary(t => t.G("id").GetInt32(), t => (Pct: Text(t, "amount_type") == "percent" ? t.D("amount") : 0m, Name: Text(t, "name") ?? ""));
            var codes = (await ReadAsync(s, "product.product", raw.Select(l => M2O(l, "product_id").Id).Where(x => x != null).Select(x => x!.Value), new[] { "default_code" }, ct))
                .ToDictionary(p => p.G("id").GetInt32(), p => Text(p, "default_code"));

            var lines = new List<ConnectorLine>();
            foreach (var l in raw)
            {
                var lt = Ids(l, "tax_ids").Where(taxes.ContainsKey).Select(t => taxes[t]).ToList();
                var exempt = lt.Count == 0 || lt.Any(t => t.Name.Contains("exempt", StringComparison.OrdinalIgnoreCase));
                var product = M2O(l, "product_id");
                var qty = l.DN("quantity") is { } q && q > 0 ? q : 1;
                lines.Add(new ConnectorLine(Text(l, "name") ?? product.Name ?? "Item", Hs(product.Id is { } pid ? codes.GetValueOrDefault(pid) : null), qty, decimal.Round(l.D("price_total"), 2),
                    exempt ? null : lt.Sum(t => t.Pct), exempt));
            }

            ConnectorBuyer? buyer = null;
            if (M2O(doc, "partner_id").Id is { } partnerId)
            {
                var p = (await ReadAsync(s, "res.partner", new[] { partnerId }, new[] { "name", "vat", "email", "phone" }, ct)).FirstOrDefault();
                var (tin, vat) = TaxNumbers(Text(p, "vat"));
                buyer = new ConnectorBuyer(Text(p, "name") ?? "Customer", tin, vat, Text(p, "email"), Text(p, "phone"));
            }
            var reference = kind == "creditnote" ? M2O(doc, "reversed_entry_id").Id?.ToString(Inv) : null;
            var head = Head(doc);
            return new ConnectorDocument(id, head.Number, head.Status, head.Date, head.Currency, head.Total, lines, buyer, reference, Text(doc, "ref"));
        }

        public async Task WriteBackAsync(ConnectorSession s, string kind, string id, ConnectorWriteBack wb, CancellationToken ct)
        {
            var moveId = int.Parse(id, Inv);
            await ExecAsync(s, "account.move", "message_post", new object[] { new[] { moveId } }, new() { ["body"] = wb.Text, ["message_type"] = "comment", ["subtype_xmlid"] = "mail.mt_note" }, ct);
            if (wb.Pdf is { Length: > 0 } pdf)
                await ExecAsync(s, "ir.attachment", "create", new object[] { new Dictionary<string, object>
                {
                    ["name"] = wb.PdfName, ["datas"] = Convert.ToBase64String(pdf), ["res_model"] = "account.move", ["res_id"] = moveId, ["mimetype"] = "application/pdf",
                } }, null, ct);
        }
    }
}
