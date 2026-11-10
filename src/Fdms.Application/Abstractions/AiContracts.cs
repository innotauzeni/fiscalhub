namespace Fdms.Application.Abstractions
{
    // =================================================================================================================
    // Gemini (the only outside AI service) and where its key lives
    // =================================================================================================================

    /// <summary>A picture or PDF sent along with a message (Gemini reads it directly).</summary>
    public sealed record GeminiInline(string MimeType, byte[] Data);
    public sealed record GeminiMessage(string Role, string Text, GeminiInline? File = null);   // Role: "user" or "model"
    public sealed record GeminiRequest(string System, IReadOnlyList<GeminiMessage> Messages, bool Json = true, int MaxTokens = 1024, double Temperature = 0.2);
    /// <summary>Error is a message fit to show a person (never the key, never the raw response).</summary>
    public sealed record GeminiResult(string? Text, string? Error, int Status = 200);

    public interface IGeminiClient
    {
        Task<GeminiResult> GenerateAsync(GeminiRequest req, CancellationToken ct);
    }

    public sealed record AiSettingsDto(bool HasKey, string? KeyHint, string Model, bool ChatEnabled, bool ReportsEnabled, bool SummariesEnabled, int DailyChatLimit, int DailyReportLimitPerCompany,
        AiUsageTodayDto Usage, DateTime? UpdatedAt);
    public sealed record AiUsageTodayDto(int Chats, int Reports, int Failures);
    public sealed record SaveAiSettingsRequest(string? ApiKey, bool ClearKey, string? Model, bool ChatEnabled, bool ReportsEnabled, bool SummariesEnabled, int DailyChatLimit, int DailyReportLimitPerCompany);
    public sealed record AiTestResultDto(bool Ok, string Message);

    /// <summary>What the rest of the system needs from the stored settings: never the key itself outside the client that calls Google.</summary>
    public sealed record AiRuntime(string? ApiKey, string Model, bool ChatEnabled, bool ReportsEnabled, bool SummariesEnabled, int DailyChatLimit, int DailyReportLimitPerCompany);

    public interface IAiSettingsService
    {
        Task<AiSettingsDto> GetAsync(CancellationToken ct);
        Task<AiSettingsDto> SaveAsync(SaveAiSettingsRequest req, CancellationToken ct);
        Task<AiTestResultDto> TestAsync(CancellationToken ct);
        Task<AiRuntime> RuntimeAsync(CancellationToken ct);
        /// <summary>Counts one use and refuses it when the day's allowance is spent. kind: chat, report or summary.</summary>
        Task UseAsync(string kind, Guid? clientId, string? ip, CancellationToken ct);
        Task RecordFailureAsync(string kind, Guid? clientId, string? ip, CancellationToken ct);
    }

    // =================================================================================================================
    // AI report builder
    // =================================================================================================================

    /// <summary>
    /// The only thing the AI is allowed to produce for a report: a choice from a closed list. Everything here is validated, and the numbers are always worked out by the gateway itself.
    /// </summary>
    public sealed record ReportSpec(string? Title, string Metric, string GroupBy, string Period, string? From, string? To, ReportFilters? Filters, int Top = 0, string Order = "auto", string Chart = "auto");
    public sealed record ReportFilters(IReadOnlyList<Guid>? DeviceIds, string? Item, string? PaymentMethod, string? Currency, string? DocumentType, string? Buyer);

    public sealed record ReportColumnDto(string Key, string Label, string Kind);   // Kind: text, money, number, percent
    public sealed record ReportResultDto(string Title, string Metric, string MetricLabel, string GroupBy, string GroupLabel, string From, string To, IReadOnlyList<ReportColumnDto> Columns,
        IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<ReportTotalDto> Totals, string Chart, int DocumentsCounted, bool Truncated, IReadOnlyList<string> Notes);
    public sealed record ReportTotalDto(string Currency, decimal Value, string Label);

    public sealed record AskReportRequest(string Question);
    public sealed record AskReportResponse(ReportSpec Spec, ReportResultDto Result, string? Summary, string? Notice);
    public sealed record RunReportRequest(ReportSpec Spec);

    public sealed record ReportChoice(string Id, string Label, string? Help = null);
    public sealed record ReportMeta(IReadOnlyList<ReportChoice> Metrics, IReadOnlyList<ReportChoice> GroupBys, IReadOnlyList<ReportChoice> Periods, IReadOnlyList<ReportChoice> Charts,
        IReadOnlyList<ReportChoice> Devices, IReadOnlyList<string> PaymentMethods, bool AiAvailable, IReadOnlyList<string> Examples);

    public sealed record SavedReportDto(Guid Id, string Name, string? Question, ReportSpec Spec, DateTime CreatedAt, string? CreatedBy);
    public sealed record SaveReportRequest(string Name, string? Question, ReportSpec Spec);

    public interface IAiReportService
    {
        Task<ReportMeta> MetaAsync(Guid clientId, CancellationToken ct);
        /// <summary>Asks Gemini to turn a plain-language question into a report spec, validates it, and runs it.</summary>
        Task<AskReportResponse> AskAsync(Guid clientId, string question, string? ip, CancellationToken ct);
        /// <summary>Runs a spec (built by hand or saved earlier). No AI involved, so it costs nothing.</summary>
        Task<ReportResultDto> RunAsync(Guid clientId, ReportSpec spec, CancellationToken ct);
        Task<IReadOnlyList<SavedReportDto>> ListSavedAsync(Guid clientId, CancellationToken ct);
        Task<SavedReportDto> SaveAsync(Guid clientId, SaveReportRequest req, CancellationToken ct);
        Task DeleteSavedAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<ReportResultDto> RunSavedAsync(Guid clientId, Guid id, CancellationToken ct);
    }

    // =================================================================================================================
    // Public onboarding assistant
    // =================================================================================================================

    public sealed record AssistantTurn(string Role, string Text);   // Role: "user" or "assistant"
    public sealed record AssistantFields(string? CompanyName, string? Tin, string? ContactName, string? ContactSurname, string? Email, string? Phone, int? PlanId, string? BillingCycle, string? Message);
    public sealed record AssistantChatRequest(IReadOnlyList<AssistantTurn> Messages, AssistantFields? Fields);
    /// <summary>
    /// Stage: collecting (still asking), confirm (everything needed is known: the page now asks for the password and shows Submit). The password is never part of the chat.
    /// </summary>
    public sealed record AssistantChatResponse(string Reply, AssistantFields Fields, IReadOnlyList<string> Missing, string Stage, IReadOnlyList<AssistantPlanDto> Plans);
    public sealed record AssistantPlanDto(int Id, string Name, string Price, string Summary);
    public sealed record AssistantStatusDto(bool Enabled, string Greeting);

    public interface IAssistantService
    {
        Task<AssistantStatusDto> StatusAsync(CancellationToken ct);
        Task<AssistantChatResponse> ChatAsync(AssistantChatRequest req, string? ip, CancellationToken ct);
    }

    // =================================================================================================================
    // User profile
    // =================================================================================================================

    public sealed record ProfileDto(string Id, string Email, string UserName, string Name, string Surname, string? Phone, string? CompanyName, IReadOnlyList<string> Roles, DateTime? LastLoginDate,
        DateTime CreatedAt, bool HasAvatar, string AccessScope, IReadOnlyList<ProfileDeviceDto> Devices);
    public sealed record ProfileDeviceDto(Guid Id, int ZimraDeviceId, string? Name, string? Branch);
    public sealed record UpdateProfileRequest(string Name, string Surname, string? Phone);
    /// <summary>Empty = sign in with the email address.</summary>
    public sealed record ChangeUserNameRequest(string? UserName);
    public sealed record ChangeEmailRequest(string NewEmail, string Password);
    public sealed record ConfirmEmailChangeRequest(string NewEmail, string Code);
    public sealed record ProfileMessageDto(string Message);
    public sealed record SessionDto(int Id, DateTime CreatedAt, DateTime ExpiresAt, string? IpAddress, string? Browser, bool Current);
    public sealed record RevokeOthersRequest(string? CurrentRefreshToken);

    public interface IProfileService
    {
        Task<ProfileDto> GetAsync(string userId, CancellationToken ct);
        Task<ProfileDto> UpdateAsync(string userId, UpdateProfileRequest req, CancellationToken ct);
        Task<ProfileDto> ChangeUserNameAsync(string userId, ChangeUserNameRequest req, CancellationToken ct);
        /// <summary>Checks the password and emails a 6-digit code to the NEW address; the email only changes once that code is entered.</summary>
        Task<ProfileMessageDto> RequestEmailChangeAsync(string userId, ChangeEmailRequest req, CancellationToken ct);
        Task<ProfileDto> ConfirmEmailChangeAsync(string userId, ConfirmEmailChangeRequest req, CancellationToken ct);
        Task SetAvatarAsync(string userId, byte[] bytes, CancellationToken ct);
        Task RemoveAvatarAsync(string userId, CancellationToken ct);
        Task<TemplateLogo?> GetAvatarAsync(string userId, CancellationToken ct);
        Task<IReadOnlyList<SessionDto>> SessionsAsync(string userId, string? currentRefreshToken, CancellationToken ct);
        Task<int> RevokeOthersAsync(string userId, string? currentRefreshToken, CancellationToken ct);
        Task RevokeSessionAsync(string userId, int sessionId, CancellationToken ct);
    }
}
