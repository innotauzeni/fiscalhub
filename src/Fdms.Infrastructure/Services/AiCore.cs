using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Where the Gemini key lives: in the database, encrypted with Data Protection (the same way payment-gateway secrets are), never returned by any endpoint, so the operator connects the AI in the
    /// console instead of editing a server file. Also the daily allowance: every use is counted, and a day's limit stops a runaway bill.
    /// </summary>
    public sealed class AiSettingsService : IAiSettingsService
    {
        public const string RowKey = "ai.gemini";
        public const string DefaultModel = "gemini-2.5-flash";
        private sealed class Stored
        {
            public string? KeyProtected { get; set; }
            public string? KeyHint { get; set; }
            public string Model { get; set; } = DefaultModel;
            public bool ChatEnabled { get; set; } = true;
            public bool ReportsEnabled { get; set; } = true;
            public bool SummariesEnabled { get; set; } = true;
            public int DailyChatLimit { get; set; } = 500;
            public int DailyReportLimitPerCompany { get; set; } = 40;
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly FdmsDbContext _db;
        private readonly IDataProtector _protector;
        private readonly IServiceProvider _sp;
        private readonly IConfiguration _cfg;
        private readonly ICallerScope _scope;
        public AiSettingsService(FdmsDbContext db, IDataProtectionProvider dp, IServiceProvider sp, IConfiguration cfg, ICallerScope scope) { _db = db; _protector = dp.CreateProtector("Fdms.Ai.v1"); _sp = sp; _cfg = cfg; _scope = scope; }

        private async Task<(Stored S, SystemSetting? Row)> LoadAsync(bool track, CancellationToken ct)
        {
            var q = track ? _db.SystemSettings : _db.SystemSettings.AsNoTracking();
            var row = await q.FirstOrDefaultAsync(x => x.Key == RowKey, ct);
            Stored s;
            try { s = row == null ? new Stored() : JsonSerializer.Deserialize<Stored>(row.Value, Json) ?? new Stored(); } catch (JsonException) { s = new Stored(); }
            if (row == null && _cfg["Gemini:Model"] is { Length: > 0 } m) s.Model = m;
            return (s, row);
        }

        private string? Key(Stored s)
        {
            if (string.IsNullOrEmpty(s.KeyProtected)) return null;
            try { return _protector.Unprotect(s.KeyProtected); } catch (System.Security.Cryptography.CryptographicException) { return null; }   // the key ring changed: treated as not set
        }

        public async Task<AiRuntime> RuntimeAsync(CancellationToken ct)
        {
            var (s, _) = await LoadAsync(false, ct);
            return new AiRuntime(Key(s), s.Model, s.ChatEnabled, s.ReportsEnabled, s.SummariesEnabled, s.DailyChatLimit, s.DailyReportLimitPerCompany);
        }

        private async Task<AiUsageTodayDto> UsageAsync(CancellationToken ct)
        {
            var day = DateTime.UtcNow.Date;
            var rows = await _db.AiUsage.AsNoTracking().Where(u => u.At >= day).GroupBy(u => new { u.Kind, u.Ok }).Select(g => new { g.Key.Kind, g.Key.Ok, N = g.Count() }).ToListAsync(ct);
            return new AiUsageTodayDto(rows.Where(r => r.Kind == "chat").Sum(r => r.N), rows.Where(r => r.Kind == "report").Sum(r => r.N), rows.Where(r => !r.Ok).Sum(r => r.N));
        }

        public async Task<AiSettingsDto> GetAsync(CancellationToken ct)
        {
            var (s, row) = await LoadAsync(false, ct);
            return new AiSettingsDto(Key(s) != null, s.KeyHint, s.Model, s.ChatEnabled, s.ReportsEnabled, s.SummariesEnabled, s.DailyChatLimit, s.DailyReportLimitPerCompany, await UsageAsync(ct), row?.UpdatedAt);
        }

        public async Task<AiSettingsDto> SaveAsync(SaveAiSettingsRequest r, CancellationToken ct)
        {
            var (s, row) = await LoadAsync(true, ct);
            var model = (r.Model ?? "").Trim();
            if (model.Length == 0) model = DefaultModel;
            if (model.Length > 60 || !System.Text.RegularExpressions.Regex.IsMatch(model, @"^[A-Za-z0-9._\-]+$")) throw GatewayException.BadRequest("MODEL_INVALID", "The model name may only contain letters, digits, dots, dashes and underscores (for example gemini-2.5-flash).");
            if (r.DailyChatLimit is < 0 or > 100_000 || r.DailyReportLimitPerCompany is < 0 or > 10_000) throw GatewayException.BadRequest("LIMIT_INVALID", "A daily limit must be between 0 and 100,000 (0 switches that part off).");

            var changes = new List<string>();
            if (r.ClearKey) { s.KeyProtected = null; s.KeyHint = null; changes.Add("key removed"); }
            else if (!string.IsNullOrWhiteSpace(r.ApiKey))
            {
                var key = r.ApiKey.Trim();
                if (key.Length < 20 || key.Length > 200 || key.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '<' or '>')) throw GatewayException.BadRequest("KEY_INVALID", "That does not look like a Gemini API key (it is a single string of about 39 characters, often starting with AIza).");
                s.KeyProtected = _protector.Protect(key); s.KeyHint = "…" + key[^4..]; changes.Add("key replaced");
            }
            if (s.Model != model) changes.Add($"model {model}");
            s.Model = model; s.ChatEnabled = r.ChatEnabled; s.ReportsEnabled = r.ReportsEnabled; s.SummariesEnabled = r.SummariesEnabled;
            s.DailyChatLimit = r.DailyChatLimit; s.DailyReportLimitPerCompany = r.DailyReportLimitPerCompany;

            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = RowKey });
            row.Value = JsonSerializer.Serialize(s, Json); row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { Action = "ai.settings", Entity = "Settings", Detail = changes.Count == 0 ? "saved" : string.Join(", ", changes) });
            await _db.SaveChangesAsync(ct);
            return await GetAsync(ct);
        }

        public async Task<AiTestResultDto> TestAsync(CancellationToken ct)
        {
            var rt = await RuntimeAsync(ct);
            if (rt.ApiKey == null) return new AiTestResultDto(false, "No API key is saved yet.");
            _db.AiUsage.Add(new AiUsageLog { Kind = "test", IpAddress = _scope.IpAddress }); await _db.SaveChangesAsync(ct);
            var res = await _sp.GetRequiredService<IGeminiClient>().GenerateAsync(new GeminiRequest("Reply with the single word OK.", new[] { new GeminiMessage("user", "ping") }, Json: false, MaxTokens: 16), ct);
            return res.Error == null ? new AiTestResultDto(true, $"Gemini answered ({rt.Model}). The key works.") : new AiTestResultDto(false, res.Error);
        }

        public async Task UseAsync(string kind, Guid? clientId, string? ip, CancellationToken ct)
        {
            var rt = await RuntimeAsync(ct);
            var day = DateTime.UtcNow.Date;
            if (kind == "chat")
            {
                if (rt.DailyChatLimit == 0) throw new GatewayException(503, "AI_OFF", "The assistant is switched off.");
                if (await _db.AiUsage.CountAsync(u => u.Kind == "chat" && u.At >= day, ct) >= rt.DailyChatLimit) throw new GatewayException(429, "AI_LIMIT", "The assistant has reached its daily limit. Please try again tomorrow, or use the registration form.");
            }
            else if (kind == "report")
            {
                if (rt.DailyReportLimitPerCompany == 0) throw new GatewayException(503, "AI_OFF", "AI reports are switched off.");
                if (clientId != null && await _db.AiUsage.CountAsync(u => u.Kind == "report" && u.ClientId == clientId && u.At >= day, ct) >= rt.DailyReportLimitPerCompany)
                    throw new GatewayException(429, "AI_LIMIT", "Your company has used today's AI report allowance. You can still build and run reports by hand, and saved reports cost nothing.");
            }
            _db.AiUsage.Add(new AiUsageLog { Kind = kind, ClientId = clientId, IpAddress = ip }); await _db.SaveChangesAsync(ct);
        }

        public async Task RecordFailureAsync(string kind, Guid? clientId, string? ip, CancellationToken ct)
        {
            var since = DateTime.UtcNow.AddMinutes(-2);
            var last = await _db.AiUsage.Where(u => u.Kind == kind && u.ClientId == clientId && u.IpAddress == ip && u.At >= since).OrderByDescending(u => u.Id).FirstOrDefaultAsync(ct);
            if (last != null) { last.Ok = false; await _db.SaveChangesAsync(ct); }
        }
    }

    // =================================================================================================================
    /// <summary>The one place that talks to Google. The key travels in a header (never in an address or a log); answers are reduced to text or a message fit to show a person.</summary>
    public sealed class GeminiClient : IGeminiClient
    {
        private readonly HttpClient _http;
        private readonly IAiSettingsService _settings;
        private readonly IConfiguration _cfg;
        private readonly ILogger<GeminiClient> _log;
        public GeminiClient(HttpClient http, IAiSettingsService settings, IConfiguration cfg, ILogger<GeminiClient> log) { _http = http; _settings = settings; _cfg = cfg; _log = log; }

        public async Task<GeminiResult> GenerateAsync(GeminiRequest req, CancellationToken ct)
        {
            var rt = await _settings.RuntimeAsync(ct);
            if (rt.ApiKey == null) return new GeminiResult(null, "The AI is not set up yet (no Gemini key saved).", 503);

            var generation = new Dictionary<string, object?> { ["temperature"] = req.Temperature, ["maxOutputTokens"] = req.MaxTokens };
            if (req.Json) generation["responseMimeType"] = "application/json";
            // Gemini 2.5 Flash "thinks" before answering and the thinking uses the same token allowance, which can cut a JSON answer off half-way: switch it off for these structured jobs
            if (rt.Model.Contains("flash", StringComparison.OrdinalIgnoreCase) && rt.Model.Contains("2.5", StringComparison.Ordinal)) generation["thinkingConfig"] = new { thinkingBudget = 0 };
            var body = new
            {
                systemInstruction = new { parts = new[] { new { text = req.System } } },
                contents = req.Messages.Select(m => new { role = m.Role == "model" ? "model" : "user", parts = Parts(m) }),
                generationConfig = generation,
            };
            var baseUrl = (_cfg["Gemini:BaseUrl"] ?? "https://generativelanguage.googleapis.com").TrimEnd('/');
            var path = $"/v1beta/models/{Uri.EscapeDataString(rt.Model)}:generateContent";
            using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            msg.Headers.Add("x-goog-api-key", rt.ApiKey);

            try
            {
                using var res = await _http.SendAsync(msg, ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                {
                    _log.LogWarning("Gemini answered {Status}", (int)res.StatusCode);
                    return new GeminiResult(null, Explain((int)res.StatusCode, text), (int)res.StatusCode);
                }
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out _)) return new GeminiResult(null, "The AI declined to answer that.", 422);
                if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0) return new GeminiResult(null, "The AI gave no answer. Try rephrasing.", 502);
                var parts = cands[0].TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var p) ? p.EnumerateArray().Select(x => x.TryGetProperty("text", out var t) ? t.GetString() : null).Where(t => t != null) : Enumerable.Empty<string?>();
                var answer = string.Concat(parts);
                if (cands[0].TryGetProperty("finishReason", out var fr) && fr.GetString() is { } reason && reason != "STOP") _log.LogWarning("Gemini finished with {Reason} after {Length} characters", reason, answer.Length);
                return answer.Length == 0 ? new GeminiResult(null, "The AI gave an empty answer. Try rephrasing.", 502) : new GeminiResult(answer, null);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new GeminiResult(null, "Gemini took too long to answer. Try again.", 504); }
            catch (HttpRequestException ex) { _log.LogWarning(ex, "Could not reach Gemini"); return new GeminiResult(null, "Could not reach Gemini. Check the server's internet connection.", 502); }
            catch (JsonException) { return new GeminiResult(null, "Gemini answered in a form this system could not read.", 502); }
        }

        private static object[] Parts(GeminiMessage m) =>
            m.File == null ? new object[] { new { text = m.Text } } : new object[] { new { text = m.Text }, new { inlineData = new { mimeType = m.File.MimeType, data = Convert.ToBase64String(m.File.Data) } } };

        private static string Explain(int status, string body)
        {
            var b = body.Length > 600 ? body[..600] : body;
            if (status == 400 && b.Contains("API key not valid", StringComparison.OrdinalIgnoreCase)) return "Gemini says the API key is not valid. Check it in the AI assistant settings.";
            return status switch
            {
                400 => "Gemini could not process the request (it may not support this model setting).",
                401 or 403 => "Gemini refused the key. Check it is correct and that the Generative Language API is enabled for it.",
                404 => "Gemini does not know that model name (or this key type). Check the model in the AI assistant settings, for example gemini-2.5-flash.",
                429 => "Gemini is busy or its quota is used up. Try again in a moment.",
                >= 500 => "Gemini had a problem on its side. Try again shortly.",
                _ => $"Gemini answered with an error ({status}).",
            };
        }
    }
}
