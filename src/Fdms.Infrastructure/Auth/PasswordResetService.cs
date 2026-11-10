using System.Net;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Auth
{
    /// <summary>Forgotten password: a 6-digit code by email (Identity's email token provider, tied to the security stamp), then a new password.</summary>
    public sealed class PasswordResetService : IPasswordResetService
    {
        private const string Purpose = "fdms-password-reset";
        private const string Sent = "If that email belongs to an account, we have sent it a 6-digit code (by email, and by SMS when the account has a phone number). It works for 5 minutes.";

        private readonly UserManager<ApplicationUser> _users;
        private readonly FdmsDbContext _db;
        private readonly IEmailSender _email;
        private readonly ISmsDispatcher _sms;
        private readonly ILogger<PasswordResetService> _log;

        public PasswordResetService(UserManager<ApplicationUser> users, FdmsDbContext db, IEmailSender email, ISmsDispatcher sms, ILogger<PasswordResetService> log)
        {
            _users = users; _db = db; _email = email; _sms = sms; _log = log;
        }

        private Task Audit(ApplicationUser user, string action, string detail, CancellationToken ct)
        {
            _db.AuditLogs.Add(new AuditLog { ClientId = user.ClientId, UserId = user.Id, UserName = user.Email, ActorType = "user", Action = action, Detail = detail, Entity = "User", EntityId = user.Id });
            return _db.SaveChangesAsync(ct);
        }

        private async Task<ApplicationUser?> FindAsync(string? email)
        {
            var e = email?.Trim();
            // the email, or the user name a person signs in with
            return string.IsNullOrEmpty(e) ? null : await _users.FindByEmailAsync(e) ?? await _users.FindByNameAsync(e);
        }

        public async Task<PasswordResetAnswer> RequestAsync(ForgotPasswordRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Email)) throw GatewayException.BadRequest("EMAIL_REQUIRED", "Enter your email or user name.");
            var user = await FindAsync(req.Email);
            if (user == null || user.Status != "ACTIVE" || string.IsNullOrEmpty(user.Email)) return new PasswordResetAnswer(Sent);

            var code = await _users.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, Purpose);
            var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(user.Name) ? "there" : user.Name);
            var html = $@"<div style=""font-family:Segoe UI,Arial,sans-serif;max-width:480px;margin:auto;color:#0f172a"">
<h2 style=""margin:0 0 12px"">Reset your password</h2>
<p>Hi {name},</p>
<p>Use this code to choose a new password for FDMS Gateway:</p>
<p style=""font-size:32px;font-weight:700;letter-spacing:8px;background:#f1f5f9;border-radius:12px;padding:16px;text-align:center"">{code}</p>
<p>It works for 5 minutes and only once. If you did not ask for it, ignore this email: your password stays the same.</p>
</div>";
            await _email.SendAsync(user.Email, "Your FDMS Gateway password reset code", html, ct);
            var sms = string.IsNullOrWhiteSpace(user.PhoneNumber) ? null
                : await _sms.SendNowAsync(user.ClientId, user.PhoneNumber, $"Your FiscalHub password reset code is {code}. It works for 5 minutes. If you did not ask for it, ignore this SMS.", "code", user.Id, "Password reset code ******", ct);
            await Audit(user, "auth.password-reset-requested", sms is { Ok: true } ? "reset code emailed and sent by SMS" : "reset code emailed", ct);
            return new PasswordResetAnswer(Sent);
        }

        public async Task<PasswordResetAnswer> ResetAsync(ResetPasswordWithCodeRequest req, CancellationToken ct)
        {
            var bad = GatewayException.BadRequest("INVALID_CODE", "That code is wrong or has expired. Ask for a new one.");
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrEmpty(req.NewPassword)) throw GatewayException.BadRequest("CODE_REQUIRED", "Enter the code from the email and your new password.");
            var user = await FindAsync(req.Email);
            if (user == null || user.Status != "ACTIVE") throw bad;
            if (await _users.IsLockedOutAsync(user)) throw new GatewayException(401, "ACCOUNT_LOCKED", "Too many failed attempts. Try again in a few minutes.");

            if (!await _users.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, Purpose, req.Code.Trim()))
            {
                await _users.AccessFailedAsync(user);
                await Audit(user, "auth.password-reset-failed", "wrong or expired reset code", ct);
                throw bad;
            }

            var token = await _users.GeneratePasswordResetTokenAsync(user);
            var r = await _users.ResetPasswordAsync(user, token, req.NewPassword);   // also renews the security stamp, so the code cannot be used again
            if (!r.Succeeded) throw GatewayException.BadRequest("PASSWORD_REJECTED", string.Join(" ", r.Errors.Select(e => e.Description)));

            await _users.ResetAccessFailedCountAsync(user);
            if (user.MustChangePassword) { user.MustChangePassword = false; await _users.UpdateAsync(user); }
            // signed out everywhere: whoever knew the old password loses their sessions
            _db.RefreshTokens.RemoveRange(await _db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync(ct));
            await Audit(user, "auth.password-reset", "password reset with an emailed code; all sessions signed out", ct);
            _log.LogInformation("Password reset for {User}", user.Email);
            return new PasswordResetAnswer("Your password was changed. Sign in with the new one.");
        }
    }
}
