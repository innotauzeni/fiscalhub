using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Fdms.Api.Security;
using Fdms.Application;
using Fdms.Application.Abstractions;
using Fdms.Infrastructure;
using Fdms.Infrastructure.Auth;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var errorSink = new Fdms.Api.Logging.ErrorLogSink();   // one per host; attached to its services once built
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext().WriteTo.Sink(errorSink));   // errors also land in the System logs page

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Device private keys are encrypted with Data Protection. The key ring MUST be persisted (and backed up) or every
// device key becomes unreadable after a restart/redeploy.
var configuredKeyRing = builder.Configuration["DataProtection:KeysPath"];
var keyRing = string.IsNullOrWhiteSpace(configuredKeyRing) ? Path.Combine(AppContext.BaseDirectory, "keys") : configuredKeyRing;
var dp = builder.Services.AddDataProtection().SetApplicationName("fdms-gateway").PersistKeysToFileSystem(new DirectoryInfo(keyRing));
if (OperatingSystem.IsWindows() && builder.Configuration.GetValue<bool>("DataProtection:UseDpapi"))
    dp.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICallerScope, HttpCallerScope>();   // replaces the null scope used by background jobs

// ---- authentication: people use a JWT, integrations use a per-device token; one policy scheme picks per request ----
builder.Services.AddAuthentication(o => { o.DefaultScheme = AuthSchemes.Smart; o.DefaultChallengeScheme = AuthSchemes.Smart; })
    .AddPolicyScheme(AuthSchemes.Smart, AuthSchemes.Smart, o => o.ForwardDefaultSelector = ctx =>
    {
        if (!string.IsNullOrEmpty(ctx.Request.Headers["X-Bridge-Key"].FirstOrDefault())) return AuthSchemes.Bridge;
        if (!string.IsNullOrEmpty(ctx.Request.Headers["X-Api-Key"].FirstOrDefault())) return AuthSchemes.ApiToken;
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (auth != null && auth.StartsWith("Bearer fhb_", StringComparison.OrdinalIgnoreCase)) return AuthSchemes.Bridge;
        return auth != null && auth.StartsWith("Bearer fdms_", StringComparison.OrdinalIgnoreCase) ? AuthSchemes.ApiToken : JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
    {
        // A suspended company or deactivated user loses access within ~20 seconds, not when the token expires.
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var uid = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var services = ctx.HttpContext.RequestServices;
                async Task<UserState> Load()
                {
                    var db = services.GetRequiredService<FdmsDbContext>();
                    var u = await db.Users.AsNoTracking().Where(x => x.Id == uid).Select(x => new { x.Status, x.ClientId, x.AccessScope }).FirstOrDefaultAsync();
                    if (u == null || u.Status != "ACTIVE") return new UserState(false, false, Array.Empty<Guid>());
                    if (u.ClientId != null && !await db.Clients.AnyAsync(c => c.Id == u.ClientId && c.IsActive)) return new UserState(false, false, Array.Empty<Guid>());
                    if (u.AccessScope != "restricted" || u.ClientId == null) return new UserState(true, false, Array.Empty<Guid>());

                    // restricted: the devices of the assigned branches plus the individually assigned devices (never another company's)
                    var branchIds = db.UserBranches.Where(x => x.UserId == uid).Select(x => x.BranchId);
                    var direct = db.UserDevices.Where(x => x.UserId == uid).Select(x => x.DeviceId);
                    var ids = await db.Devices.AsNoTracking()
                        .Where(d => d.ClientId == u.ClientId && ((d.BranchId != null && branchIds.Contains(d.BranchId.Value)) || direct.Contains(d.Id)))
                        .Select(d => d.Id).ToListAsync();
                    return new UserState(true, true, ids);
                }
                var ttl = services.GetRequiredService<IConfiguration>().GetValue("Auth:ActiveCacheSeconds", 20);
                var state = ttl <= 0 ? await Load() : await services.GetRequiredService<IMemoryCache>().GetOrCreateAsync("active-user:" + uid, e =>
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl);
                    return Load();
                });
                if (state is not { Active: true }) { ctx.Fail("This account is no longer active."); return; }
                if (state.Restricted && ctx.Principal?.Identity is ClaimsIdentity identity)
                {
                    identity.AddClaim(new Claim(ClaimNames.Restricted, "1"));
                    foreach (var id in state.Devices) identity.AddClaim(new Claim(ClaimNames.DeviceId, id.ToString()));
                }
            },
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(AuthSchemes.ApiToken, _ => { })
    .AddScheme<AuthenticationSchemeOptions, BridgeKeyHandler>(AuthSchemes.Bridge, _ => { });

// the signing key is read from the final configuration when the options are first used (see JwtKeyProvider)
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<JwtKeyProvider>((o, k) =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = k.Issuer, ValidateAudience = true, ValidAudience = k.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k.Key)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = ClaimTypes.Name, RoleClaimType = ClaimTypes.Role,
    };
});

builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

// The web console may be hosted on its own site (e.g. https://fiscal.example.com) and call this API from there; list those addresses in Cors:Origins.
builder.Services.AddCors();
builder.Services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<IConfiguration>((o, cfg) => o.AddPolicy("console", p =>
{
    var origins = Fdms.Api.Security.CorsOrigins.From(cfg);   // read from the final configuration (appsettings, environment, host settings)
    if (origins.Length == 0) return;                          // nothing listed: only same-site calls, as before
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("X-Gateway-Url", "Content-Disposition").SetPreflightMaxAge(TimeSpan.FromMinutes(10));
}));

builder.Services.AddScoped<IApprovalExecutor, Fdms.Api.Controllers.ApprovalExecutor>();

builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddProblemDetails();

// Per-token (or per-IP when unauthenticated) rate limit, plus a tight limit on sign-in attempts.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var who = ctx.Request.Headers["X-Api-Key"].FirstOrDefault() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(who, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimit:PerMinute", 120), Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    });
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anon", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue("RateLimit:LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
    }));
    // the public sign-up chat: each message costs a Gemini call, so a person gets a handful a minute, not a flood
    o.AddPolicy("assistant", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anon", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue("RateLimit:AssistantPerMinute", 30), Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
    }));
});

builder.Services.AddHealthChecks().AddDbContextCheck<FdmsDbContext>("database");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "FDMS Fiscalization Gateway", Version = "v1",
        Description = "Step-by-step integration guide (public): /app/docs. System-agnostic ZIMRA FDMS fiscalization. People sign in (POST /api/v1/auth/login) and use the JWT as a Bearer token; integrations use a per-device API token in X-Api-Key." });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Api-Key" });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>(),
    });
});

var app = builder.Build();
errorSink.Attach(app.Services);


// Behind a reverse proxy / load balancer the real caller address arrives in X-Forwarded-For (needed for the audit trail's IP column).
if (app.Configuration.GetValue<bool>("Hosting:BehindProxy"))
{
    var fwd = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto };
    fwd.KnownNetworks.Clear(); fwd.KnownProxies.Clear();   // the proxy is the only thing that can reach this app
    app.UseForwardedHeaders(fwd);
}

_ = app.Services.GetRequiredService<JwtKeyProvider>();   // fail fast when the signing key is missing in production

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<FdmsDbContext>().Database.MigrateAsync();
}
if (app.Configuration.GetValue("Seed:Enabled", true)) await DatabaseSeeder.SeedAsync(app.Services);

// Request logging wraps the exception handler, so a handled business error is logged as a plain 4xx line (no stack trace).
app.UseSerilogRequestLogging();
// Outside everything else: a browser's preflight is answered before rate limits and sign-in checks, and error answers keep their CORS headers.
app.UseCors("console");
app.UseExceptionHandler();
// The console (/app) is a static Angular build; scripts may only come from this origin, so injected script cannot read the tokens it holds.
app.Use(async (ctx, next) =>
{
    // every answer: no MIME sniffing, never framed (clickjacking), no referrer leaks, no powerful browser features, HTTPS only once seen over HTTPS
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    if (ctx.Request.IsHttps && !app.Environment.IsDevelopment()) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";   // answers carry company data: never kept by a shared proxy
    if (ctx.Request.Path.StartsWithSegments("/app"))
    {
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data: blob:; connect-src 'self'; manifest-src 'self'; worker-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next();
});
// Static files before routing: otherwise the /app fallback endpoint matches first and the middleware skips every real file.
app.UseDefaultFiles();
// Installable app (PWA): the service worker, the manifest and the app shell must always be re-checked so an update reaches people; hashed build files never change, so they are cached for a year.
static void AppCaching(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext c)
{
    var name = c.File.Name;
    var h = c.Context.Response.Headers;
    if (name is "sw.js" or "manifest.webmanifest" or "offline.html" or "offline.js" or "index.html") h["Cache-Control"] = "no-cache";
    else if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(?:chunk|main|styles|polyfills)-[A-Za-z0-9]{8}.(?:js|css)$")) h["Cache-Control"] = "public, max-age=31536000, immutable";
}
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = AppCaching });
app.UseRouting();
app.UseRateLimiter();
// the web console: served here at /app/, or on its own site when Hosting:AppUrl names one (then the API home page sends people there)
var consoleUrl = app.Configuration["Hosting:AppUrl"]?.Trim().TrimEnd('/');
app.MapGet("/", () => Results.Redirect(string.IsNullOrEmpty(consoleUrl) ? "/app/" : consoleUrl + "/")).ExcludeFromDescription();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseMiddleware<Fdms.Api.Security.ActivityLogMiddleware>();
app.UseMiddleware<Fdms.Api.Security.BillingGateMiddleware>();
app.UseMiddleware<Fdms.Api.Security.PlanModuleMiddleware>();   // only the modules the company's plan includes
app.UseMiddleware<Fdms.Api.Security.MfaGateMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");
app.MapFallbackToFile("/app/{*path}", "app/index.html", new StaticFileOptions { OnPrepareResponse = AppCaching });   // Angular client-side routes

app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        var configured = app.Configuration["Hosting:PublicUrl"];
        string? candidate = !string.IsNullOrWhiteSpace(configured) ? configured : null;
        if (candidate == null && app.Environment.IsDevelopment())
        {
            // the address this process is actually listening on (https preferred), e.g. https://localhost:7167
            var listening = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? Array.Empty<string>();
            candidate = listening.OrderByDescending(a => a.StartsWith("https", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
                ?.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost").Replace("+", "localhost").Replace("*", "localhost");
        }
        if (candidate == null) return;
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().EnsureDefaultAsync(candidate, default);
    }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Could not store the default public API address"); }
}));

app.Run();

public partial class Program { }

/// <summary>What the JWT check learns about a user (cached ~20s): still active, and if limited, which devices they may reach.</summary>
public sealed record UserState(bool Active, bool Restricted, IReadOnlyList<Guid> Devices);
