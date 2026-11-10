using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>
    /// Payment gateway credentials kept in the database, so the operator connects a gateway in the console instead of editing server files. Secrets are
    /// encrypted with ASP.NET Data Protection (the same key ring that protects device keys) and are never returned by the API. When nothing is saved for a
    /// gateway, the server configuration file is used as a fallback (sections Paynow / PaynowSettings, EcoCashEip, StripeSettings, with the same key names as
    /// the other Fundo/BIKKI applications use), so an existing deployment keeps working.
    /// To add another gateway, add a definition here and an adapter that calls <see cref="IPaymentGatewaySettings.CredentialsAsync"/>.
    /// </summary>
    public sealed class PaymentGatewaySettings : IPaymentGatewaySettings
    {
        private sealed record FieldDef(string Key, string Label, bool Secret, bool Required, string? Hint = null, string? Default = null);
        private sealed record GatewayDef(string Code, string Name, string Description, bool Automatic, FieldDef[] Fields, string? CallbackPath, string[] ConfigSections);

        public const string PaynowCode = "paynow", EcoCashCode = "ecocash", StripeCode = "stripe", EcoCashManualCode = "ecocash-manual", PayPalCode = "paypal";
        private const string DefaultPaynowUrl = "https://www.paynow.co.zw";
        private const string DefaultEcoCashUrl = "https://developers.ecocash.co.zw/sandbox/payment/v1";

        private static readonly GatewayDef[] Gateways =
        {
            new(PaynowCode, "Paynow", "Visa, Mastercard, ZimSwitch, EcoCash, OneMoney, InnBucks and other wallets, through one merchant integration from paynow.co.zw.", true,
                new FieldDef[]
                {
                    new("integrationId", "Integration ID", false, true, "From your Paynow merchant dashboard (a number)."),
                    new("integrationKey", "Integration key", true, true, "The secret key of the same integration. Stored encrypted; never shown again."),
                    new("baseUrl", "Paynow address", false, false, "Leave empty for the live address.", DefaultPaynowUrl),
                }, "/api/v1/payments/paynow/result", new[] { "Paynow", "PaynowSettings" }),

            new(EcoCashCode, "EcoCash (direct)", "Econet's own merchant gateway (EcoCash EIP, developers.ecocash.co.zw): a payment prompt goes straight to the customer's phone.", true,
                new FieldDef[]
                {
                    new("username", "API username", false, true, "Basic-auth user of your EcoCash EIP app."),
                    new("password", "API password", true, true, "Stored encrypted; never shown again."),
                    new("merchantCode", "Merchant code", false, true),
                    new("merchantPin", "Merchant PIN", true, true, "Stored encrypted; never shown again."),
                    new("merchantNumber", "Merchant number", false, true, "Your registered EcoCash merchant number."),
                    new("merchantName", "Merchant name", false, false),
                    new("superMerchantName", "Super merchant name", false, false),
                    new("baseUrl", "EcoCash API address", false, false, "The sandbox address is the default. Use the live address EcoCash gave you in production.", DefaultEcoCashUrl),
                    new("terminalId", "Terminal id", false, false, null, "TERM001"),
                    new("location", "Location", false, false, null, "Harare"),
                    new("channel", "Channel", false, false, null, "WEB"),
                    new("countryCode", "Country code", false, false, null, "ZW"),
                }, "/api/v1/payments/ecocash/notify", new[] { "EcoCashEip" }),

            new(StripeCode, "Card (Stripe)", "International Visa/Mastercard payments on Stripe's hosted checkout page.", true,
                new FieldDef[]
                {
                    new("secretKey", "Secret key", true, true, "sk_live_… or sk_test_… from your Stripe dashboard. Stored encrypted; never shown again."),
                    new("webhookSecret", "Webhook signing secret", true, false, "whsec_… of the webhook endpoint pointing at the URL below. Without it payments are still confirmed when the customer returns."),
                }, "/api/v1/payments/stripe/webhook", new[] { "StripeSettings" }),

            new(EcoCashManualCode, "EcoCash (Send Money)", "No integration: customers dial a Send Money code to your own EcoCash number, then tell us the reference. You confirm each payment.", false,
                new FieldDef[] { new("number", "EcoCash number", false, true, "Your registered EcoCash number, e.g. 0771234567.") }, null, Array.Empty<string>()),

            new(PayPalCode, "PayPal", "No integration: customers send the amount to your PayPal address, then tell us the reference. You confirm each payment.", false,
                new FieldDef[] { new("email", "PayPal email", false, true, "The PayPal address customers pay.") }, null, Array.Empty<string>()),
        };

        private sealed class Stored { public bool Enabled { get; set; } public Dictionary<string, string?> Values { get; set; } = new(); }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly FdmsDbContext _db;
        private readonly IDataProtector _protector;
        private readonly IConfiguration _cfg;
        private readonly IMemoryCache _cache;
        private readonly ICallerScope _scope;

        public PaymentGatewaySettings(FdmsDbContext db, IDataProtectionProvider dp, IConfiguration cfg, IMemoryCache cache, ICallerScope scope)
        { _db = db; _protector = dp.CreateProtector("Fdms.PaymentGateway.v1"); _cfg = cfg; _cache = cache; _scope = scope; }

        private static string RowKey(string code) => "Gateway:" + code;
        private static string CacheKey(string code) => "gateway-creds:" + code;
        private static GatewayDef Def(string code) => Gateways.FirstOrDefault(g => g.Code == code) ?? throw GatewayException.NotFound("Payment gateway");

        /// <summary>Server-file fallback, so an existing deployment keeps working until credentials are saved in the console. Honours an Enabled: false in the file.</summary>
        private Dictionary<string, string?>? FromConfig(GatewayDef d)
        {
            foreach (var name in d.ConfigSections)
            {
                var sec = _cfg.GetSection(name);
                if (!sec.Exists() || string.Equals(sec["Enabled"], "false", StringComparison.OrdinalIgnoreCase)) continue;
                var vals = d.Fields.ToDictionary(f => f.Key, f => (string?)sec[char.ToUpperInvariant(f.Key[0]) + f.Key[1..]]);
                if (d.Fields.Where(f => f.Required).All(f => !string.IsNullOrWhiteSpace(vals[f.Key]))) return vals;
            }
            return null;
        }

        private async Task<(Stored? Row, SystemSetting? Setting)> LoadRowAsync(string code, CancellationToken ct)
        {
            var s = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == RowKey(code), ct);
            if (s == null) return (null, null);
            try { return (JsonSerializer.Deserialize<Stored>(s.Value, Json), s); } catch (JsonException) { return (null, s); }
        }

        private string? Reveal(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try { return _protector.Unprotect(stored); } catch (System.Security.Cryptography.CryptographicException) { return null; }   // key ring changed: treat as not set rather than crash
        }

        private static bool Complete(GatewayDef d, IReadOnlyDictionary<string, string?> v) => d.Fields.Where(f => f.Required).All(f => !string.IsNullOrWhiteSpace(v.GetValueOrDefault(f.Key)));

        public async Task<IReadOnlyDictionary<string, string>?> CredentialsAsync(string code, CancellationToken ct)
        {
            if (_cache.TryGetValue(CacheKey(code), out IReadOnlyDictionary<string, string>? hit)) return hit;
            var d = Def(code);
            var (row, _) = await LoadRowAsync(code, ct);
            Dictionary<string, string?>? source = null;
            if (row != null) { if (row.Enabled) source = d.Fields.ToDictionary(f => f.Key, f => f.Secret ? Reveal(row.Values.GetValueOrDefault(f.Key)) : row.Values.GetValueOrDefault(f.Key)); }
            else source = FromConfig(d);

            IReadOnlyDictionary<string, string>? result = null;
            if (source != null && Complete(d, source))
            {
                var vals = d.Fields.ToDictionary(f => f.Key, f => string.IsNullOrWhiteSpace(source.GetValueOrDefault(f.Key)) ? f.Default ?? "" : source[f.Key]!.Trim());
                if (code == PaynowCode) vals["baseUrl"] = vals["baseUrl"].TrimEnd('/').Replace("/interface", "", StringComparison.OrdinalIgnoreCase);   // other apps keep the /interface suffix in the address
                result = vals;
            }
            _cache.Set(CacheKey(code), result, TimeSpan.FromSeconds(20));
            return result;
        }

        public async Task<IReadOnlyList<PayMethodDto>> MethodsAsync(CancellationToken ct)
        {
            var list = new List<PayMethodDto>();
            if (await CredentialsAsync(PaynowCode, ct) != null)
            {
                list.Add(new("paynow-web", "Card, ZimSwitch & wallets", "redirect", true, "Paynow"));
                list.Add(new("paynow-ecocash", "EcoCash (via Paynow)", "mobile", true, null));
                list.Add(new("paynow-onemoney", "OneMoney", "mobile", true, null));
            }
            if (await CredentialsAsync(EcoCashCode, ct) != null) list.Add(new("ecocash", "EcoCash", "mobile", true, null));
            if (await CredentialsAsync(StripeCode, ct) != null) list.Add(new("stripe", "Card (Stripe)", "redirect", true, "International cards"));
            if (await CredentialsAsync(EcoCashManualCode, ct) is { } eco) list.Add(new("ecocash-manual", "EcoCash (Send Money)", "manual", false, eco["number"]));
            if (await CredentialsAsync(PayPalCode, ct) is { } pp) list.Add(new("paypal", "PayPal", "manual", false, pp["email"]));
            return list;
        }

        private async Task<PaymentGatewayDto> ToDtoAsync(GatewayDef d, string apiBase, CancellationToken ct)
        {
            var (row, setting) = await LoadRowAsync(d.Code, ct);
            var cfg = row == null ? FromConfig(d) : null;
            var values = row?.Values ?? cfg ?? new Dictionary<string, string?>();
            var fields = d.Fields.Select(f =>
            {
                var raw = values.GetValueOrDefault(f.Key);
                var has = !string.IsNullOrWhiteSpace(raw) && (!f.Secret || row == null || Reveal(raw) != null);
                return new GatewayFieldDto(f.Key, f.Label, f.Secret, f.Required, f.Secret ? null : (raw ?? f.Default), has, f.Hint);
            }).ToList();
            var enabled = row?.Enabled ?? cfg != null;
            var plain = d.Fields.ToDictionary(f => f.Key, f => f.Secret ? (fields.First(x => x.Key == f.Key).HasValue ? "x" : null) : values.GetValueOrDefault(f.Key));
            return new PaymentGatewayDto(d.Code, d.Name, d.Description, d.Automatic, enabled, enabled && Complete(d, plain), row != null ? "database" : cfg != null ? "config" : "none", fields,
                d.CallbackPath == null ? null : apiBase.TrimEnd('/') + d.CallbackPath, setting?.UpdatedAt, setting?.UpdatedBy);
        }

        public async Task<IReadOnlyList<PaymentGatewayDto>> ListAsync(string apiBase, CancellationToken ct)
        {
            var list = new List<PaymentGatewayDto>();
            foreach (var d in Gateways) list.Add(await ToDtoAsync(d, apiBase, ct));
            return list;
        }

        private static string? Validate(GatewayDef d, string key, string value)
        {
            var label = d.Fields.First(f => f.Key == key).Label;
            if (value.Length > 300 || value.Any(char.IsControl)) return $"{label} is not valid.";
            if (d.Code == PaynowCode && key == "integrationId" && !value.All(char.IsDigit)) return "The Paynow integration ID is a number.";
            if (key == "baseUrl")
            {
                // the address is where payments are sent and polled: only the gateway itself, over https
                var domain = d.Code == PaynowCode ? "paynow.co.zw" : "ecocash.co.zw";
                if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !(u.Host == domain || u.Host.EndsWith("." + domain)))
                    return $"The {d.Name} address must be an https address on {domain} (leave it empty for the default).";
            }
            if (d.Code == StripeCode && key == "secretKey" && !(value.StartsWith("sk_") || value.StartsWith("rk_")))
                return value.StartsWith("pk_") ? "That is the publishable key (pk_...). Paste the SECRET key (sk_live_... or sk_test_...) from Stripe > Developers > API keys." : "A Stripe secret key starts with sk_live_ or sk_test_. If the box was filled in by your browser, clear it and paste the key from Stripe > Developers > API keys.";
            if (d.Code == StripeCode && key == "webhookSecret" && !value.StartsWith("whsec_")) return "A Stripe webhook signing secret starts with whsec_.";
            if (d.Code == PayPalCode && key == "email" && !System.Text.RegularExpressions.Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return "Enter a valid PayPal email address.";
            if (d.Code == EcoCashManualCode && key == "number" && new string(value.Where(char.IsDigit).ToArray()).Length is < 9 or > 13) return "Enter the EcoCash number, e.g. 0771234567.";
            return null;
        }

        public async Task<PaymentGatewayDto> SaveAsync(string code, SaveGatewayRequest req, string apiBase, CancellationToken ct)
        {
            var d = Def(code);
            var oldRow = (await LoadRowAsync(code, ct)).Row;
            var baseline = oldRow?.Values ?? FromConfig(d) ?? new Dictionary<string, string?>();   // what the non-secret fields were before this save
            var next = new Stored { Enabled = req.Enabled, Values = new() };
            var changed = new List<string>();
            foreach (var f in d.Fields)
            {
                var given = req.Values?.GetValueOrDefault(f.Key)?.Trim();
                if (f.Secret)
                {
                    if (req.ClearSecrets) { next.Values[f.Key] = null; if (oldRow?.Values.GetValueOrDefault(f.Key) != null) changed.Add(f.Key + " cleared"); }
                    else if (!string.IsNullOrEmpty(given))
                    {
                        var bad = Validate(d, f.Key, given); if (bad != null) throw GatewayException.BadRequest("GATEWAY_FIELD_INVALID", bad);
                        next.Values[f.Key] = _protector.Protect(given); changed.Add(f.Key + " replaced");
                    }
                    else if (oldRow != null) next.Values[f.Key] = oldRow.Values.GetValueOrDefault(f.Key);
                    else if (FromConfig(d) is { } fromFile && !string.IsNullOrWhiteSpace(fromFile.GetValueOrDefault(f.Key))) { next.Values[f.Key] = _protector.Protect(fromFile[f.Key]!); changed.Add(f.Key + " taken over from the server file"); }
                }
                else
                {
                    var v = string.IsNullOrEmpty(given) ? null : given;
                    if (v != null) { var bad = Validate(d, f.Key, v); if (bad != null) throw GatewayException.BadRequest("GATEWAY_FIELD_INVALID", bad); }
                    next.Values[f.Key] = v;
                    if (v != baseline.GetValueOrDefault(f.Key)) changed.Add($"{f.Key} set to {v ?? "(default)"}");
                }
            }
            if (next.Enabled && !Complete(d, d.Fields.ToDictionary(f => f.Key, f => f.Secret ? Reveal(next.Values.GetValueOrDefault(f.Key)) : next.Values.GetValueOrDefault(f.Key))))
                throw GatewayException.BadRequest("GATEWAY_INCOMPLETE", $"Fill in {string.Join(", ", d.Fields.Where(f => f.Required).Select(f => f.Label))} before enabling {d.Name}.");
            if (oldRow?.Enabled != next.Enabled) changed.Add(next.Enabled ? "enabled" : "disabled");

            var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == RowKey(code), ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = RowKey(code) });
            row.Value = JsonSerializer.Serialize(next, Json); row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { Action = "gateway.update", Entity = "PaymentGateway", EntityId = code, Detail = changed.Count == 0 ? "saved without changes" : string.Join(", ", changed) });   // never the secret itself
            await _db.SaveChangesAsync(ct);
            _cache.Remove(CacheKey(code));
            return await ToDtoAsync(d, apiBase, ct);
        }
    }
}
