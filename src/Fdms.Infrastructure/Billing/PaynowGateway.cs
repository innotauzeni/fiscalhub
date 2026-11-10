using System.Globalization;
using MailKit.Security;
using MimeKit;
using System.Security.Cryptography;
using System.Text;
using Fdms.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Billing
{
    public sealed record PaynowInit(bool Ok, string? RedirectUrl, string? PollUrl, string? Instructions, string? PaynowReference, string? Error);

    /// <summary>The outcome Paynow reports for a transaction (from a poll or from its callback).</summary>
    public sealed record PaynowStatus(string Status, string? Reference, string? PaynowReference, decimal Amount, string? PollUrl)
    {
        public bool IsPaid => Status.Equals("Paid", StringComparison.OrdinalIgnoreCase) || Status.Equals("Awaiting Delivery", StringComparison.OrdinalIgnoreCase) || Status.Equals("Delivered", StringComparison.OrdinalIgnoreCase);
        public bool IsFailed => Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) || Status.Equals("Disputed", StringComparison.OrdinalIgnoreCase) || Status.Equals("Refunded", StringComparison.OrdinalIgnoreCase) || Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Paynow (paynow.co.zw): one integration for Visa/Mastercard, ZimSwitch, EcoCash, OneMoney, InnBucks and other wallets.</summary>
    public interface IPaynowGateway
    {
        /// <summary>True when Paynow is enabled and its credentials are set (in the console, or in the server configuration).</summary>
        Task<bool> IsConfiguredAsync(CancellationToken ct);
        /// <param name="method">null = the Paynow checkout page (customer picks cards, ZimSwitch, wallets); ecocash / onemoney = a payment prompt on the phone.</param>
        Task<PaynowInit> InitiateAsync(string reference, decimal amount, string email, string info, string returnUrl, string resultUrl, string? method, string? phone, CancellationToken ct);
        Task<PaynowStatus?> PollAsync(string pollUrl, CancellationToken ct);
        /// <summary>Checks the hash of a message Paynow posted to our result URL. False = not from Paynow (or tampered).</summary>
        Task<PaynowStatus?> ParseResultAsync(IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken ct);
    }

    public sealed class PaynowGateway : IPaynowGateway
    {
        private readonly HttpClient _http;
        private readonly IPaymentGatewaySettings _settings;
        private readonly ILogger<PaynowGateway> _log;

        public PaynowGateway(HttpClient http, IPaymentGatewaySettings settings, ILogger<PaynowGateway> log) { _http = http; _settings = settings; _log = log; }

        private sealed record Creds(string Id, string Key, string BaseUrl);

        /// <summary>Read on every call (cached briefly), so credentials saved in the console apply without a restart.</summary>
        private async Task<Creds?> CredsAsync(CancellationToken ct)
        {
            var c = await _settings.CredentialsAsync(PaymentGatewaySettings.PaynowCode, ct);
            return c == null ? null : new Creds(c["integrationId"], c["integrationKey"], c["baseUrl"].TrimEnd('/'));
        }

        public async Task<bool> IsConfiguredAsync(CancellationToken ct) => await CredsAsync(ct) != null;

        /// <summary>Uppercase SHA-512 of all the values (in order) followed by the integration key, as Paynow specifies.</summary>
        public static string Hash(IEnumerable<string> values, string key) =>
            Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(string.Concat(values) + key))).ToUpperInvariant();

        public async Task<PaynowInit> InitiateAsync(string reference, decimal amount, string email, string info, string returnUrl, string resultUrl, string? method, string? phone, CancellationToken ct)
        {
            var c = await CredsAsync(ct);
            if (c == null) return new PaynowInit(false, null, null, null, null, "Paynow is not configured.");
            var fields = new List<KeyValuePair<string, string>>
            {
                new("id", c.Id), new("reference", reference), new("amount", amount.ToString("0.00", CultureInfo.InvariantCulture)), new("additionalinfo", info),
                new("returnurl", returnUrl), new("resulturl", resultUrl), new("authemail", email),
            };
            if (method != null) { fields.Add(new("phone", phone ?? "")); fields.Add(new("method", method)); }
            fields.Add(new("status", "Message"));
            fields.Add(new("hash", Hash(fields.Select(f => f.Value), c.Key)));

            try
            {
                using var res = await _http.PostAsync($"{c.BaseUrl}/interface/{(method == null ? "initiatetransaction" : "remotetransaction")}", new FormUrlEncodedContent(fields), ct);
                var body = Parse(await res.Content.ReadAsStringAsync(ct));
                string Get(string k) => body.FirstOrDefault(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value ?? "";
                if (!Get("status").Equals("Ok", StringComparison.OrdinalIgnoreCase)) return new PaynowInit(false, null, null, null, null, Get("error") is { Length: > 0 } e ? e : "Paynow refused the payment.");
                if (!VerifyResponse(body, c.Key)) return new PaynowInit(false, null, null, null, null, "Paynow's answer failed its signature check.");
                return new PaynowInit(true, Get("browserurl") is { Length: > 0 } b ? b : null, Get("pollurl"), Get("instructions") is { Length: > 0 } i ? i : null, Get("paynowreference") is { Length: > 0 } p ? p : null, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning(ex, "Could not reach Paynow");
                return new PaynowInit(false, null, null, null, null, "Paynow is not reachable right now. Try again in a moment.");
            }
        }

        public async Task<PaynowStatus?> PollAsync(string pollUrl, CancellationToken ct)
        {
            var c = await CredsAsync(ct);
            if (c == null || !Uri.TryCreate(pollUrl, UriKind.Absolute, out var u) || !u.Host.EndsWith(new Uri(c.BaseUrl).Host, StringComparison.OrdinalIgnoreCase)) return null;   // only ever poll Paynow itself
            try
            {
                using var res = await _http.PostAsync(u, new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded"), ct);
                var body = Parse(await res.Content.ReadAsStringAsync(ct));
                return Parse(body, c);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _log.LogWarning(ex, "Could not poll Paynow"); return null; }
        }

        public async Task<PaynowStatus?> ParseResultAsync(IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken ct)
        {
            var c = await CredsAsync(ct);
            return c == null ? null : Parse(fields, c);
        }

        private static PaynowStatus? Parse(IReadOnlyList<KeyValuePair<string, string>> fields, Creds c)
        {
            if (!VerifyResponse(fields, c.Key)) return null;
            string Get(string k) => fields.FirstOrDefault(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value ?? "";
            if (!decimal.TryParse(Get("amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) amount = 0;
            var status = new PaynowStatus(Get("status"), Get("reference") is { Length: > 0 } r ? r : null, Get("paynowreference") is { Length: > 0 } p ? p : null, amount, Get("pollurl") is { Length: > 0 } u ? u : null);
            return status.Status.Length > 0 ? status : null;
        }

        private static bool VerifyResponse(IReadOnlyList<KeyValuePair<string, string>> fields, string key)
        {
            var given = fields.FirstOrDefault(x => x.Key.Equals("hash", StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrEmpty(given)) return false;
            var expected = Hash(fields.Where(x => !x.Key.Equals("hash", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value), key);
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given.ToUpperInvariant()));
        }

        private static List<KeyValuePair<string, string>> Parse(string body) =>
            body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0].Replace('+', ' ')), Uri.UnescapeDataString((p.Length > 1 ? p[1] : "").Replace('+', ' ')))).ToList();
    }

    /// <summary>SMTP email (Email:Host, Port, User, Password, From, EnableSsl). With no host configured it only logs, so the rest of the system works without email.</summary>
    public sealed class SmtpEmailSender : IEmailSender
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<SmtpEmailSender> _log;
        private readonly IServiceScopeFactory _scopes;
        private readonly Services.EmailSettingsStore _store;
        public SmtpEmailSender(IConfiguration cfg, ILogger<SmtpEmailSender> log, IServiceScopeFactory scopes, Services.EmailSettingsStore store) { _cfg = cfg; _log = log; _scopes = scopes; _store = store; }

        /// <summary>Settings are read from "Email" (Host, Port, EnableSsl, User, Password, From) or, so a block copied from another ERP works as it is, from "SmtpClientSetting" (Enabled, Host, Port, EnableSsl, fromUsername, fromPassword, FromAddress, FromName).</summary>
        /// <remarks>The mail server set in Settings (database) wins; the configuration file is the fallback.</remarks>
        private string? Get(string email, string smtp)
        {
            if (_store.Current is { } s) return email switch
            {
                "Host" => s.Host, "Port" => s.Port.ToString(), "EnableSsl" => s.EnableSsl.ToString(), "User" => s.User, "Password" => _store.Password, "From" => s.FromAddress, _ => null,
            };
            return _cfg["Email:" + email] is { Length: > 0 } v ? v : _cfg["SmtpClientSetting:" + smtp] is { Length: > 0 } w ? w : null;
        }

        public bool Configured => _store.Current is { } s ? s.Enabled && !string.IsNullOrWhiteSpace(s.Host) : !string.IsNullOrWhiteSpace(Get("Host", "Host")) && _cfg.GetValue("SmtpClientSetting:Enabled", true);

        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct) => SendAsync(to, subject, htmlBody, Array.Empty<EmailAttachment>(), ct);

        /// <summary>Background mail (alerts, billing, sign-ups): a mail problem must never fail the thing that caused it, and a temporary one is queued and retried instead of lost.</summary>
        public async Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct)
        {
            var r = await DeliverAsync(to, subject, htmlBody, attachments, ct);
            if (r.Ok) return;
            _log.LogWarning("Could not send email '{Subject}' to {To}: {Problem}", subject, to, r.Error);
            if (!r.Temporary || !Configured) return;
            try { using var scope = _scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<IEmailOutbox>().EnqueueAsync(null, to, subject, htmlBody, attachments, r.Error, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not queue the email for a retry"); }
        }

        public async Task<string?> TrySendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct) => (await DeliverAsync(to, subject, htmlBody, attachments, ct)).Error;

        public async Task<EmailResult> DeliverAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct)
        {
            if (!Configured || string.IsNullOrWhiteSpace(to)) return new EmailResult(false, false, "Email is not set up on this server.");
            try
            {
                var user = Get("User", "fromUsername");
                var address = Get("From", "FromAddress") ?? user ?? "noreply@localhost";
                var port = int.TryParse(Get("Port", "Port"), out var p) ? p : 587;
                var ssl = !bool.TryParse(Get("EnableSsl", "EnableSsl"), out var e) || e;
                var name = _store.Current is { } st ? st.FromName : _cfg["SmtpClientSetting:FromName"];

                var msg = new MimeMessage();
                msg.From.Add(string.IsNullOrWhiteSpace(name) ? MailboxAddress.Parse(address) : new MailboxAddress(name, address));
                msg.To.Add(MailboxAddress.Parse(to));
                msg.Subject = subject;
                var body = new BodyBuilder { HtmlBody = htmlBody };
                foreach (var att in attachments) body.Attachments.Add(att.FileName, att.Bytes, ContentType.Parse(att.ContentType));
                msg.Body = body.ToMessageBody();

                // port 465 wraps the whole connection in SSL from the first byte; 587 (and others) start plain and upgrade with STARTTLS; no SSL means no encryption at all
                var security = !ssl ? SecureSocketOptions.None : port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
                // some mail servers wait a long time before greeting (one takes about 25 seconds), so the wait is generous and can be changed
                using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = Math.Clamp(_cfg.GetValue("SmtpClientSetting:TimeoutSeconds", 60), 5, 180) * 1000 };
                await smtp.ConnectAsync(Get("Host", "Host"), port, security, ct);
                if (!string.IsNullOrEmpty(user)) await smtp.AuthenticateAsync(user, Get("Password", "fromPassword") ?? "", ct);
                await smtp.SendAsync(msg, ct);
                await smtp.DisconnectAsync(true, ct);
                return new EmailResult(true, false, null);
            }
            catch (MailKit.Net.Smtp.SmtpCommandException ex)
            {
                _log.LogWarning(ex, "SMTP refused '{Subject}' to {To}: {Code} {Msg}", subject, to, (int)ex.StatusCode, ex.Message);
                var temporary = (int)ex.StatusCode is >= 400 and < 500;
                return new EmailResult(false, temporary, "The mail server did not accept it: " + ex.Message + (temporary ? " (a temporary problem on the mail server)" : ""));
            }
            catch (MailKit.Security.AuthenticationException ex)
            {
                _log.LogWarning(ex, "SMTP login failed for {User}", Get("User", "fromUsername"));
                return new EmailResult(false, false, "The mail server rejected the username or password. Check fromUsername and fromPassword.");
            }
            catch (Exception ex) when (ex is MailKit.Net.Smtp.SmtpProtocolException or System.Net.Sockets.SocketException or TimeoutException or IOException or MailKit.Security.SslHandshakeException)
            {
                _log.LogWarning(ex, "SMTP connection problem for '{Subject}' to {To}", subject, to);
                return new EmailResult(false, true, "The mail server could not be reached or the secure connection failed (check Host, Port and EnableSsl: port 465 uses SSL straight away, 587 upgrades with STARTTLS). " + ex.Message);
            }
            catch (FormatException ex) { return new EmailResult(false, false, "That does not look like a valid email address: " + ex.Message); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not send email '{Subject}' to {To}", subject, to);
                return new EmailResult(false, false, "The email could not be sent: " + ex.Message);
            }
        }
    }
}
