using System.Threading.RateLimiting;
using AiObservatory.Api;
using AiObservatory.Api.Endpoints;
using AiObservatory.Api.Services;
using AiObservatory.Api.Services.Fx;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);

// The shared Anthropic rate table that ships with AiObservatory.Data. The API prices
// Anthropic events at ingest (see EventsEndpoints.RecordEventAsync), so it needs the same
// rates as the Ingest worker — from the same physical file, so they cannot drift.
//
// Resolved against AppContext.BaseDirectory (where the build drops it), NOT the content
// root: WebApplicationFactory points the content root at the project's SOURCE directory,
// so a relative path resolves somewhere the file has never existed and every WAF test
// fails at host construction. Absolute-from-the-assembly works for both.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "pricing.anthropic.json"), optional: false, reloadOnChange: false);

if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}
builder.Services.AddOpenApi();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
});

var dbConnection = builder.Configuration["DB_CONNECTION"]
    ?? throw new InvalidOperationException("DB_CONNECTION configuration is missing.");
builder.Services.AddDataLayer(dbConnection);

// Self-host safety: refuse to boot outside Development with the documented
// placeholder admin key, so an exposed deploy can't run with a guessable
// credential. The local Docker demo runs in Development and may keep "change-me".
var adminKey = builder.Configuration["OBSERVATORY_API_KEY"];
if (!builder.Environment.IsDevelopment() && (string.IsNullOrWhiteSpace(adminKey) || adminKey == "change-me"))
{
    throw new InvalidOperationException(
        "OBSERVATORY_API_KEY must be set to a non-default value outside Development.");
}
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

// Bound unconditionally: Anthropic events are priced server-side at ingest, so a missing
// or empty table is a boot-time failure rather than a silent fallback to wrong rates.
builder.Services.AddOptions<AnthropicPricingOptions>()
    .Bind(builder.Configuration.GetSection(AnthropicPricingOptions.SectionName))
    .Validate(o => o.Pricing.Count > 0, $"{AnthropicPricingOptions.SectionName}:Pricing must have at least one entry")
    // An unmatched model prices at FallbackPricing, so a zeroed fallback would silently
    // record every unknown model at $0 — the opposite of failing closed.
    .Validate(o => o.FallbackPricing.Input > 0 && o.FallbackPricing.Output > 0,
        $"{AnthropicPricingOptions.SectionName}:FallbackPricing must have positive Input and Output rates")
    // CacheWrite1h binds to 0 when a row omits it, which would bill one-hour cache writes as
    // free. Since this deployment's cache writes are ~100% one-hour, that failure would be
    // both silent and total — so an incomplete row must stop the app, not start it.
    .Validate(o => o.Pricing.TrueForAll(e => e.CacheWrite1h > 0) && o.FallbackPricing.CacheWrite1h > 0,
        $"{AnthropicPricingOptions.SectionName}: every Pricing entry and FallbackPricing must set a positive CacheWrite1h")
    .ValidateOnStart();

builder.Services.AddTransient<MailKit.Net.Smtp.ISmtpClient, MailKit.Net.Smtp.SmtpClient>();
builder.Services.AddTransient<IAlertNotifier, EmailAlertNotifier>();
builder.Services.AddScoped<BudgetAlertService>();
builder.Services.AddScoped<AdversarialReviewService>();
builder.Services.AddSingleton<AnthropicIntelligenceClient>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<InsightResponseParser>();
builder.Services.AddScoped<IInsightGenerator, InsightGenerator>();
builder.Services.AddMemoryCache();
// FxRateProvider is a transient typed client; only consume from scoped/transient services.
builder.Services.AddHttpClient<FxRateProvider>();
builder.Services.AddHostedService<IntelligenceWorkerService>();

// Fixed-window rate limit per client IP on the /api group, so an unauthenticated GET or a
// hot loop can't hammer the B1 plan. Partitioning on the (real, post-UseForwardedHeaders)
// remote IP rather than the X-Observatory-Key header is deliberate: the limiter runs before
// ApiKeyEndpointFilter, so the header is unvalidated at this point — keying on it let an
// anonymous caller mint a fresh 120/min bucket per request just by rotating a random header,
// bypassing the limit entirely.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx =>
    {
        var partitionKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

// Honour X-Forwarded-For from a trusted reverse proxy (the nginx sidecar in the
// Docker self-host topology) so the rate limiter partitions by the real client IP
// rather than collapsing every proxied caller onto the bridge IP. Only proxies on
// private networks are trusted, so a public client cannot spoof the header.
builder.Services.Configure<ForwardedHeadersOptions>(Program.ConfigureForwardedHeaders);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(builder.Configuration["SWA_ORIGIN"] ?? "https://fpaiobs-swa.azurestaticapps.net")
     .AllowAnyMethod().AllowAnyHeader()));

// Entra (Azure AD) JWT bearer auth for human callers. Only wired when configured —
// local dev runs without AzureAd settings, so the app starts and the API-key path
// (or no key, in dev) governs access. In production an authenticated user bypasses
// the API key entirely (see ApiKeyEndpointFilter); machine callers keep using keys.
var authEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"]);
if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
    await db.Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyEndpointFilter>().RequireRateLimiting("api");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    api.MapPost("/dev/seed", async (AiObservatoryDbContext db, IClock clock, CancellationToken ct) =>
    {
        // Idempotent: only seed a genuinely empty database. The Docker compose seed
        // service calls this on every `up`; the guard must cover EVERY table the seed
        // writes, not just DailyAggregates — a self-hoster who created a Subscription or
        // BudgetRule (neither of which writes a DailyAggregate) before any aggregation
        // would otherwise have that config silently destroyed on the next `up`.
        if (await db.DailyAggregates.AnyAsync(ct)
            || await db.Subscriptions.AnyAsync(ct)
            || await db.Insights.AnyAsync(ct)
            || await db.BudgetRules.AnyAsync(ct)
            || await db.UsageEvents.AnyAsync(ct))
        {
            return Results.Ok("Already seeded — skipping (data present).");
        }

        // No TRUNCATE: the guard above proves all seeded tables are empty, so the previous
        // destructive reset is unnecessary (and was the data-loss vector it guarded against).
        var today = clock.GetCurrentInstant().InUtc().Date;

        for (int i = 0; i < 14; i++)
        {
            var date = today.PlusDays(-i);

            db.DailyAggregates.Add(new DailyAggregate
            {
                Date = date,
                Provider = Provider.Anthropic,
                Model = "claude-3-5-sonnet",
                InputTokens = 150000 + i * 1000,
                OutputTokens = 80000 + i * 500,
                CacheReadTokens = 30000 + i * 200,
                CacheWriteTokens = 12000 + i * 100,
                CostUsd = 2.50m + i * 0.15m,
                RequestCount = 50 + i
            });

            db.DailyAggregates.Add(new DailyAggregate
            {
                Date = date,
                Provider = Provider.Anthropic,
                Model = "claude-3-5-haiku",
                InputTokens = 300000 + i * 2000,
                OutputTokens = 150000 + i * 1000,
                CacheReadTokens = 80000 + i * 500,
                CacheWriteTokens = 25000 + i * 150,
                CostUsd = 0.80m + i * 0.05m,
                RequestCount = 120 + i
            });

            db.DailyAggregates.Add(new DailyAggregate
            {
                Date = date,
                Provider = Provider.Google,
                Model = "gemini-1.5-pro",
                InputTokens = 80000 + i * 500,
                OutputTokens = 40000 + i * 200,
                CacheReadTokens = 15000 + i * 100,
                CacheWriteTokens = 5000 + i * 50,
                CostUsd = 1.20m + i * 0.08m,
                RequestCount = 30 + i
            });

            db.DailyAggregates.Add(new DailyAggregate
            {
                Date = date,
                Provider = Provider.Google,
                Model = "gemini-1.5-flash",
                InputTokens = 500000 + i * 5000,
                OutputTokens = 250000 + i * 2000,
                CacheReadTokens = 120000 + i * 1000,
                CacheWriteTokens = 45000 + i * 400,
                CostUsd = 0.40m + i * 0.02m,
                RequestCount = 200 + i
            });

            db.DailyAggregates.Add(new DailyAggregate
            {
                Date = date,
                Provider = Provider.Copilot,
                Model = "copilot-chat",
                InputTokens = 40000 + i * 200,
                OutputTokens = 20000 + i * 100,
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CostUsd = 0.00m,
                RequestCount = 15 + i
            });
        }

        db.Subscriptions.Add(new Subscription
        {
            Provider = Provider.Copilot,
            Name = "GitHub Copilot Business",
            CostAmount = 19.00m,
            Currency = "USD",
            BillingDay = 1,
            ActiveFrom = today.PlusDays(-60),
            ActiveTo = null
        });

        db.Subscriptions.Add(new Subscription
        {
            Provider = Provider.Anthropic,
            Name = "Claude Pro",
            CostAmount = 18.00m,
            Currency = "GBP",
            BillingDay = 15,
            ActiveFrom = today.PlusDays(-30),
            ActiveTo = null,
            ExtraUsageCost = 5.50m
        });

        db.BudgetRules.Add(new BudgetRule
        {
            Provider = null,
            Period = BillingPeriod.Daily,
            ThresholdUsd = 5.00m
        });

        db.BudgetRules.Add(new BudgetRule
        {
            Provider = Provider.Anthropic,
            Period = BillingPeriod.Monthly,
            ThresholdUsd = 150.00m
        });

        db.Insights.Add(new Insight
        {
            GeneratedAt = clock.GetCurrentInstant(),
            PeriodStart = today.PlusDays(-7),
            PeriodEnd = today,
            InsightType = InsightType.Anomaly,
            Title = "Spend Spike on Claude 3.5 Sonnet",
            Body = "Your Anthropic API cost spiked by 45% yesterday compared to the previous 7-day average. This was driven by a large batch code generation task.",
            Data = "{\"spikePercent\":45}"
        });

        db.Insights.Add(new Insight
        {
            GeneratedAt = clock.GetCurrentInstant().Minus(Duration.FromHours(2)),
            PeriodStart = today.PlusDays(-7),
            PeriodEnd = today,
            InsightType = InsightType.Efficiency,
            Title = "Gemini Flash Cache Hits High",
            Body = "Google Gemini 1.5 Flash query cache hit rate reached 82%, saving approximately $12.40 in input token costs over the past 3 days.",
            Data = "{\"savingsUsd\":12.4}"
        });

        db.Insights.Add(new Insight
        {
            GeneratedAt = clock.GetCurrentInstant().Minus(Duration.FromHours(5)),
            PeriodStart = today.PlusDays(-7),
            PeriodEnd = today,
            InsightType = InsightType.Recommendation,
            Title = "Switch simple chat completions to Haiku",
            Body = "43% of your Claude 3.5 Sonnet requests contain prompts under 200 tokens with low complexity. Switching these to Claude 3.5 Haiku could reduce your Anthropic spend by $15.50/month.",
            Data = "{\"potentialSavingsUsd\":15.5}"
        });

        await db.SaveChangesAsync(ct);
        return Results.Ok("Seed successful");
    });
}

api.MapEventsEndpoints();
api.MapCavemanEndpoints();
api.MapActivityEndpoints();
api.MapGitHubActivityEndpoints();
api.MapAdversarialReviewEndpoints();
api.MapAggregatesEndpoints();
api.MapInsightsEndpoints();
api.MapSubscriptionsEndpoints();
api.MapBudgetRulesEndpoints();

await app.RunAsync();

// Test-enabling: `public partial class Program` exposes the top-level-statement entry
// point so WebApplicationFactory<Program> can host it; ConfigureForwardedHeaders is
// pulled out to a named, independently testable method (same body as before, no
// behaviour change) so a spoofed-XFF test can build the exact same ForwardedHeadersOptions
// without spinning up the whole app.
// Required as the WebApplicationFactory<TEntryPoint> marker for integration tests.
// ReSharper disable once ClassNeverInstantiated.Global
public partial class Program
{
    protected Program() { }

    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions o)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        o.ForwardLimit = 1;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse("10.0.0.0"), 8));
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12));
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse("192.168.0.0"), 16));
    }
}
