using AiObservatory.Api;
using AiObservatory.Api.Endpoints;
using AiObservatory.Api.Services;
using AiObservatory.Api.Services.Fx;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry();
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
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<IAlertNotifier, WebhookAlertNotifier>();
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

// Fixed-window rate limit per caller (API key if present, else remote IP) on the /api
// group, so an unauthenticated GET or a hot loop can't hammer the B1 plan.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx =>
    {
        var partitionKey = ctx.Request.Headers["X-Observatory-Key"].ToString() is { Length: > 0 } key
            ? key
            : ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

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
    api.MapPost("/dev/seed", async (AiObservatoryDbContext db, IClock clock) =>
    {
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"DailyAggregates\", \"Subscriptions\", \"Insights\", \"BudgetRules\", \"UsageEvents\" RESTART IDENTITY CASCADE;");

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

        await db.SaveChangesAsync();
        return Results.Ok("Seed successful");
    });
}

api.MapEventsEndpoints();
api.MapCavemanEndpoints();
api.MapAdversarialReviewEndpoints();
api.MapAggregatesEndpoints();
api.MapInsightsEndpoints();
api.MapSubscriptionsEndpoints();
api.MapBudgetRulesEndpoints();

await app.RunAsync();
