using AiObservatory.Data;
using AiObservatory.Ingest;
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.Copilot;
using AiObservatory.Ingest.Services.Google;
using AiObservatory.Ingest.Services.OpenAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;

        var connectionString = cfg["DB_CONNECTION"]
            ?? throw new InvalidOperationException("DB_CONNECTION is required");
        services.AddDataLayer(connectionString);
        services.AddSingleton<IClock>(SystemClock.Instance);

        services.Configure<IngestOptions>(cfg.GetSection(IngestOptions.SectionName));

        // Anthropic — enabled when ANTHROPIC_BILLING_KEY is set.
        // Requires a workspace admin API key (different from the standard API key).
        var anthropicKey = cfg["ANTHROPIC_BILLING_KEY"];
        if (!string.IsNullOrEmpty(anthropicKey))
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
        if (!string.IsNullOrEmpty(githubToken) && !string.IsNullOrEmpty(copilotOrg))
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
                var http = factory.CreateClient(typeof(ICopilotUsageClient).FullName ?? nameof(ICopilotUsageClient));
                return new CopilotUsageClient(http, copilotOrg);
            });
            services.AddScoped<CopilotIngestionService>();
        }

        // Google — enabled when GOOGLE_BILLING_ACCOUNT_ID is set.
        // Also requires GOOGLE_APPLICATION_CREDENTIALS pointing at a service account key.
        // See GoogleBillingClient.cs for full setup instructions.
        var googleBillingAccount = cfg["GOOGLE_BILLING_ACCOUNT_ID"];
        if (!string.IsNullOrEmpty(googleBillingAccount))
        {
            services.AddHttpClient<IGoogleBillingClient, GoogleBillingClient>(c =>
            {
                c.BaseAddress = new Uri("https://cloudbilling.googleapis.com");
            });
            services.AddScoped<IGoogleBillingClient>(sp =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                var http = factory.CreateClient(typeof(IGoogleBillingClient).FullName ?? nameof(IGoogleBillingClient));
                return new GoogleBillingClient(http, googleBillingAccount);
            });
            services.AddScoped<GoogleIngestionService>();
        }

        // OpenAI — enabled when OPENAI_ADMIN_KEY is set.
        // Requires an admin API key with the openai.usage.read permission.
        // Create one at platform.openai.com/api-keys (type: Admin key).
        var openAiAdminKey = cfg["OPENAI_ADMIN_KEY"];
        if (!string.IsNullOrEmpty(openAiAdminKey))
        {
            services.AddHttpClient<IOpenAiUsageClient, OpenAiUsageClient>(c =>
            {
                c.BaseAddress = new Uri("https://api.openai.com");
                c.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiAdminKey}");
            });
            services.AddScoped<OpenAiIngestionService>();
        }

        services.AddHostedService<ProviderPollingWorkerService>();
    })
    .Build();

await host.RunAsync();
