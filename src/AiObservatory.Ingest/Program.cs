using AiObservatory.Data;
using AiObservatory.Ingest;
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.Copilot;
using AiObservatory.Ingest.Services.GitHub;
using AiObservatory.Ingest.Services.Google;
using AiObservatory.Ingest.Services.OpenAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;

        // A Key Vault reference that fails to resolve (secret absent, or the app has no
        // access) is left by App Service as the literal "@Microsoft.KeyVault(...)" string.
        // That is non-empty, so a plain IsNullOrEmpty gate would enable the provider with a
        // garbage credential and 401 hourly forever. Treat such a value as unset.
        static bool IsConfigured(string? value) =>
            !string.IsNullOrEmpty(value) &&
            !value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase);

        var connectionString = cfg["DB_CONNECTION"]
            ?? throw new InvalidOperationException("DB_CONNECTION is required");
        services.AddDataLayer(connectionString);
        services.AddSingleton<IClock>(SystemClock.Instance);

        services.Configure<IngestOptions>(cfg.GetSection(IngestOptions.SectionName));

        // Anthropic — enabled when ANTHROPIC_BILLING_KEY is set.
        // Requires a workspace admin API key (different from the standard API key).
        var anthropicKey = cfg["ANTHROPIC_BILLING_KEY"];
        if (IsConfigured(anthropicKey))
        {
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
        var githubRepoAllowlist = cfg.GetSection($"{IngestOptions.SectionName}:{nameof(IngestOptions.GitHubRepoAllowlist)}").Get<string[]>() ?? [];
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

        services.AddHostedService<ProviderPollingWorkerService>();
    })
    .Build();

await host.RunAsync();
