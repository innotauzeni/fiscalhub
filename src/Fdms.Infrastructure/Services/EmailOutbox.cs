using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Emails the mail server could not take yet. They wait here and are tried again on a growing schedule (2 min, 10 min, 30 min, 1 h, 2 h, 4 h, 8 h, 12 h), like webhook deliveries.
    /// After the last try the email is given up on and, when it belongs to a company, an alert says so. Nothing in here ever blocks the work that caused the email.
    /// </summary>
    public sealed class EmailOutboxService : IEmailOutbox
    {
        public static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8), TimeSpan.FromHours(12) };
        private sealed record Att(string Name, string Type, string Data);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly FdmsDbContext _db;
        private readonly IEmailSender _email;
        private readonly INotificationService _notify;
        private readonly ILogger<EmailOutboxService> _log;
        public EmailOutboxService(FdmsDbContext db, IEmailSender email, INotificationService notify, ILogger<EmailOutboxService> log) { _db = db; _email = email; _notify = notify; _log = log; }

        public async Task EnqueueAsync(Guid? clientId, string to, string subject, string html, IReadOnlyList<EmailAttachment> files, string? firstError, CancellationToken ct)
        {
            _db.EmailOutbox.Add(new EmailOutbox
            {
                ClientId = clientId, To = to.Length > 200 ? to[..200] : to, Subject = subject.Length > 300 ? subject[..300] : subject, Html = html, LastError = Cut(firstError), NextAttemptAt = DateTime.UtcNow.Add(Backoff[0]),
                AttachmentsJson = files.Count == 0 ? null : JsonSerializer.Serialize(files.Select(f => new Att(f.FileName, f.ContentType, Convert.ToBase64String(f.Bytes))), Json),
            });
            await _db.SaveChangesAsync(ct);
        }

        private static string? Cut(string? s) => s is { Length: > 600 } ? s[..600] : s;
        private static OutboxDto ToDto(EmailOutbox e) => new(e.Id, e.To, e.Subject, e.Status, e.Attempts, e.NextAttemptAt, e.LastError, e.CreatedAt, e.SentAt);

        public async Task<IReadOnlyList<OutboxDto>> ListAsync(Guid clientId, CancellationToken ct) =>
            (await _db.EmailOutbox.AsNoTracking().Where(e => e.ClientId == clientId && e.Status != "Cancelled").OrderByDescending(e => e.CreatedAt).Take(30).ToListAsync(ct)).Select(ToDto).ToList();

        public async Task RetryNowAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var e = await _db.EmailOutbox.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Email");
            if (e.Status is "Sent" or "Cancelled") throw GatewayException.Conflict("EMAIL_DONE", "That email is already " + e.Status.ToLowerInvariant() + ".");
            e.Status = "Pending"; e.NextAttemptAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task CancelAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var e = await _db.EmailOutbox.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Email");
            if (e.Status == "Sent") throw GatewayException.Conflict("EMAIL_DONE", "That email was already sent.");
            e.Status = "Cancelled"; await _db.SaveChangesAsync(ct);
        }

        public async Task<int> RunDueAsync(DateTime utcNow, CancellationToken ct)
        {
            var delivered = 0;
            // a few at a time: some mail servers take a minute per message
            var due = await _db.EmailOutbox.Where(e => e.Status == "Pending" && e.NextAttemptAt <= utcNow).OrderBy(e => e.NextAttemptAt).Take(5).ToListAsync(ct);
            foreach (var e in due)
            {
                e.NextAttemptAt = utcNow.AddMinutes(10);   // claim it, so an overlapping pass does not send it twice
                await _db.SaveChangesAsync(ct);
                var files = string.IsNullOrEmpty(e.AttachmentsJson) ? new List<EmailAttachment>()
                    : (JsonSerializer.Deserialize<List<Att>>(e.AttachmentsJson, Json) ?? new()).Select(a => new EmailAttachment(a.Name, Convert.FromBase64String(a.Data), a.Type)).ToList();
                var r = await _email.DeliverAsync(e.To, e.Subject, e.Html, files, ct);
                e.Attempts++;
                if (r.Ok) { e.Status = "Sent"; e.SentAt = DateTime.UtcNow; e.LastError = null; delivered++; }
                else
                {
                    e.LastError = Cut(r.Error);
                    if (!r.Temporary || e.Attempts >= Backoff.Length)
                    {
                        e.Status = "Failed";
                        if (e.ClientId is { } cid) await _notify.RaiseAsync(cid, "email", "bad", $"email:{e.Id}", $"An email to {e.To} could not be delivered", $"{e.Subject}. {e.LastError}", "/alerts", ct);
                    }
                    else e.NextAttemptAt = utcNow.Add(Backoff[e.Attempts]);
                }
                await _db.SaveChangesAsync(ct);
            }
            return delivered;
        }
    }

    /// <summary>Retries queued emails. Looks every minute.</summary>
    public sealed class EmailOutboxJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<EmailOutboxJob> _log;
        public EmailOutboxJob(IServiceScopeFactory scopes, ILogger<EmailOutboxJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { return; }
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<IEmailOutbox>().RunDueAsync(DateTime.UtcNow, stop);
                    if (n > 0) _log.LogInformation("Delivered {N} queued email(s)", n);
                    await Task.Delay(TimeSpan.FromMinutes(1), stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Email outbox run failed"); try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (OperationCanceledException) { break; } }
            }
        }
    }
}
