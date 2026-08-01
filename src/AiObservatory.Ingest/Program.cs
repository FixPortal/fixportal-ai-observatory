// Explicit: the Worker SDK's implicit usings do not include ASP.NET Core's, which arrive
// here via a FrameworkReference rather than the Web SDK (see the csproj for why).
using AiObservatory.Data;
using AiObservatory.Data.Pricing;
using AiObservatory.Ingest;
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.Copilot;
using AiObservatory.Ingest.Services.GitHub;
using AiObservatory.Ingest.Services.Google;
using AiObservatory.Ingest.Services.OpenAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NodaTime;

var host = Host.CreateDefaultBuilder(args)
    // Anthropic rates come from the shared table that ships with AiObservatory.Data, not
    // from this worker's appsettings, so the worker and the API cannot drift apart.
    // Resolved against AppContext.BaseDirectory (where the build drops it) rather than the
    // content root, which a test host or a non-default working directory can move.
    .ConfigureAppConfiguration(cfg =>
        cfg.AddJsonFile(
            Path.Combine(AppContext.BaseDirectory, "pricing.anthropic.json"),
            optional: false,
            reloadOnChange: false
        )
    )
    .ConfigureServices(
        (ctx, services) =>
        {
            var cfg = ctx.Configuration;

            // A Key Vault reference that fails to resolve (secret absent, or the app has no
            // access) is left by App Service as the literal "@Microsoft.KeyVault(...)" string.
            // That is non-empty, so a plain IsNullOrEmpty gate would enable the provider with a
            // garbage credential and 401 hourly forever. Treat such a value as unset.
            static bool IsConfigured(string? value) =>
                !string.IsNullOrEmpty(value)
                && !value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase);

            var connectionString =
                cfg["DB_CONNECTION"] ?? throw new InvalidOperationException("DB_CONNECTION is required");
            services.AddDataLayer(connectionString);
            services.AddSingleton<IClock>(SystemClock.Instance);

            services.Configure<IngestOptions>(cfg.GetSection(IngestOptions.SectionName));

            // The allowlist may arrive as a delimited scalar (deployed Key Vault reference)
            // rather than an array, which plain section binding cannot handle. Resolve it
            // once here and post-configure it in, so the registration gate below and every
            // consumer of IngestOptions see the same value.
            var githubRepoAllowlist = IngestOptions.ResolveGitHubRepoAllowlist(cfg);
            services.PostConfigure<IngestOptions>(o => o.GitHubRepoAllowlist = githubRepoAllowlist);

            // Anthropic — enabled when ANTHROPIC_BILLING_KEY is set.
            // Requires a workspace admin API key (different from the standard API key).
            var anthropicKey = cfg["ANTHROPIC_BILLING_KEY"];
            if (IsConfigured(anthropicKey))
            {
                services
                    .AddOptions<AnthropicPricingOptions>()
                    .Bind(cfg.GetSection(AnthropicPricingOptions.SectionName))
                    .ValidateDataAnnotations()
                    .Validate(
                        o => o.Pricing.Count > 0,
                        $"{AnthropicPricingOptions.SectionName}:Pricing must have at least one entry"
                    )
                    // An unmatched model prices at FallbackPricing, so a zeroed fallback would
                    // silently record every unknown model at $0 — the opposite of failing closed.
                    .Validate(
                        o => o.FallbackPricing is { Input: > 0, Output: > 0 },
                        $"{AnthropicPricingOptions.SectionName}:FallbackPricing must have positive Input and Output rates"
                    )
                    .Validate(
                        o => o.HasPositiveCacheWrite1HRates(),
                        $"{AnthropicPricingOptions.SectionName}: every Pricing entry and FallbackPricing must set a positive CacheWrite1h"
                    )
                    .ValidateOnStart();

                services.AddHttpClient<IAnthropicUsageClient, AnthropicUsageClient>(c =>
                {
                    c.BaseAddress = new Uri("https://api.anthropic.com");
                    c.DefaultRequestHeaders.Add("x-api-key", anthropicKey);
                    c.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                });
                services.AddScoped<AnthropicIngestionService>();
            }

            // Copilot — enabled when GITHUB_TOKEN and COPILOT_ORG are both set.
            // GITHUB_TOKEN requires the manage_billing:copilot scope.
            var githubToken = cfg["GITHUB_TOKEN"];
            var copilotOrg = cfg["COPILOT_ORG"];
            if (IsConfigured(githubToken) && IsConfigured(copilotOrg))
            {
                services.AddHttpClient<ICopilotUsageClient, CopilotUsageClient>(c =>
                {
                    c.BaseAddress = new Uri("https://api.github.com");
                    c.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
                    c.DefaultRequestHeaders.Add("User-Agent", "fpaiobs-ingest");
                    c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                });
                services.AddScoped<ICopilotUsageClient>(sp =>
                {
                    var factory = sp.GetRequiredService<IHttpClientFactory>();
                    var http = factory.CreateClient(nameof(ICopilotUsageClient));
                    return new CopilotUsageClient(http, copilotOrg!);
                });
                services.AddScoped<CopilotIngestionService>();
            }

            // Google — enabled when GOOGLE_BILLING_ACCOUNT_ID is set.
            // Also requires GOOGLE_APPLICATION_CREDENTIALS pointing at a service account key.
            // See GoogleBillingClient.cs for full setup instructions.
            var googleBillingAccount = cfg["GOOGLE_BILLING_ACCOUNT_ID"];
            if (IsConfigured(googleBillingAccount))
            {
                services.AddHttpClient<IGoogleBillingClient, GoogleBillingClient>(c =>
                {
                    c.BaseAddress = new Uri("https://cloudbilling.googleapis.com");
                });
                services.AddScoped<IGoogleBillingClient>(sp =>
                {
                    var factory = sp.GetRequiredService<IHttpClientFactory>();
                    var http = factory.CreateClient(nameof(IGoogleBillingClient));
                    return new GoogleBillingClient(http, googleBillingAccount!);
                });
                services.AddScoped<GoogleIngestionService>();
            }

            // OpenAI — enabled when OPENAI_ADMIN_KEY is set.
            // Requires an admin API key with the openai.usage.read permission.
            // Create one at platform.openai.com/api-keys (type: Admin key).
            var openAiAdminKey = cfg["OPENAI_ADMIN_KEY"];
            if (IsConfigured(openAiAdminKey))
            {
                services
                    .AddOptions<OpenAiPricingOptions>()
                    .Bind(cfg.GetSection(OpenAiPricingOptions.SectionName))
                    .ValidateDataAnnotations()
                    .Validate(
                        o => o.Pricing.Count > 0,
                        $"{OpenAiPricingOptions.SectionName}:Pricing must have at least one entry"
                    )
                    .ValidateOnStart();

                services.AddHttpClient<IOpenAiUsageClient, OpenAiUsageClient>(c =>
                {
                    c.BaseAddress = new Uri("https://api.openai.com");
                    c.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiAdminKey}");
                });
                services.AddScoped<OpenAiIngestionService>();
            }

            // GitHub Activity — enabled when GITHUB_TOKEN is set AND at least one repo is
            // allowlisted. Reuses the same GITHUB_TOKEN as Copilot metrics; this PAT now also
            // needs contents:read, pull-requests:read, actions:read (in addition to
            // manage_billing:copilot if Copilot metrics are also enabled).
            if (IsConfigured(githubToken) && githubRepoAllowlist.Length > 0)
            {
                services.AddHttpClient<IGitHubActivityClient, GitHubActivityClient>(c =>
                {
                    c.BaseAddress = new Uri("https://api.github.com");
                    c.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
                    c.DefaultRequestHeaders.Add("User-Agent", "fpaiobs-ingest");
                    c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                });
                services.AddScoped<GitHubIngestionService>();
            }

            // Telemetry, gated on the connection string exactly as the API gates it. Worth
            // having for its own sake, but specifically: this worker spent its entire deployed
            // life failing to start, and the absence of telemetry made that read as a quiet
            // worker rather than a dead one. Without this, the next failure is just as silent.
            if (!string.IsNullOrEmpty(cfg["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            {
                services.AddApplicationInsightsTelemetry();
            }

            // Singleton first, then handed to the hosted-service registration, so /healthz can
            // read the SAME instance the host is running. AddHostedService<T>() alone would
            // construct a second, unrelated one.
            services.AddSingleton<ProviderPollingWorkerService>();
            services.AddHostedService(sp => sp.GetRequiredService<ProviderPollingWorkerService>());
        }
    )
    // Minimal web host so the process answers Linux App Service's startup probe. The
    // probe's port comes from the environment (App Service sets ASPNETCORE_URLS; locally
    // launchSettings.json picks 5040) and is deliberately NOT hardcoded here -- pinning
    // 8080 would collide with whatever else a developer happens to be running.
    //
    // Endpoints are health only. This worker must never grow an API surface: it holds
    // provider credentials that the public-facing API does not, and every route added
    // here is one more thing exposed on a host that exists purely to keep the container
    // alive.
    .ConfigureWebHostDefaults(web => web.Configure(MapHealthEndpoint))
    .Build();

await host.RunAsync();

// Split out of the builder chain rather than inlined as nested lambdas: three levels of
// nesting inside ConfigureWebHostDefaults pushed this file's cognitive complexity from
// under the limit to 25.
static void MapHealthEndpoint(IApplicationBuilder app)
{
    app.UseRouting();
    app.UseEndpoints(endpoints => endpoints.MapGet("/healthz", ReportHealth));
}

static IResult ReportHealth(ProviderPollingWorkerService worker)
{
    // 503 on exactly one condition: the poll loop is no longer running while the host still
    // is. ExecuteTask completing -- faulted, cancelled, or simply returning -- is
    // unambiguous silent death, and is precisely the failure this whole change exists to
    // stop going unnoticed. App Service replacing the instance is the right response.
    var running = worker.ExecuteTask is { IsCompleted: false };

    // Deliberately NOT unhealthy on a stale LastCycleCompletedAt. The poll interval is
    // configurable, so a long legitimate gap would make App Service recycle a perfectly
    // healthy container -- recreating the very restart loop this change removes. Staleness
    // is reported for a human to judge, not acted on automatically.
    var body = new
    {
        status = running ? "healthy" : "unhealthy",
        service = "AiObservatory.Ingest",
        workerRunning = running,
        cyclesCompleted = worker.CyclesCompleted,
        lastCycleCompletedAt = worker.LastCycleCompletedAt?.ToString(),
    };

    return running ? Results.Ok(body) : Results.Json(body, statusCode: 503);
}

// Required as the WebApplicationFactory<TEntryPoint> marker for composition-root tests.
// ReSharper disable once ClassNeverInstantiated.Global
public partial class Program
{
    protected Program() { }
}
