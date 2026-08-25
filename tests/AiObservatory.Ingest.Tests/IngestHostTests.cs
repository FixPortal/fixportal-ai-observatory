using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.Copilot;
using AiObservatory.Ingest.Services.GitHub;
using AiObservatory.Ingest.Services.Google;
using AiObservatory.Ingest.Services.OpenAi;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Ingest.Tests;

[CollectionDefinition("IngestHost", DisableParallelization = true)]
public class IngestHostCollection;

[Collection("IngestHost")]
public class IngestHostTests
{
    private const string KeyVaultReference = "@Microsoft.KeyVault(VaultName=fpaiobs-kv;SecretName=test)";

    [Fact]
    public async Task StartupFailsWhenTheDatabaseConnectionIsMissing()
    {
        await using var factory = new IngestFactory { DatabaseConnection = null };

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown!, "DB_CONNECTION").Should().BeTrue();
    }

    [Theory]
    [InlineData("ANTHROPIC_BILLING_KEY", UsageSourceIds.AnthropicUsageApi)]
    [InlineData("COPILOT_ORG", UsageSourceIds.CopilotOrgReport)]
    [InlineData("GOOGLE_BILLING_ACCOUNT_ID", UsageSourceIds.GoogleCloudBillingExport)]
    [InlineData("OPENAI_ADMIN_KEY", UsageSourceIds.OpenAiUsageApi)]
    [InlineData("OPENAI_ADMIN_KEY", UsageSourceIds.OpenAiCostsApi)]
    [InlineData("GITHUB_TOKEN", UsageSourceIds.GitHubActivityApi)]
    public async Task UnresolvedKeyVaultReferenceDoesNotEnableAProvider(string setting, string sourceId)
    {
        await using var factory = new IngestFactory();
        factory.Settings[setting] = KeyVaultReference;
        if (setting == "COPILOT_ORG")
        {
            factory.Settings["GITHUB_TOKEN"] = "configured-token";
        }
        if (setting == "GITHUB_TOKEN")
        {
            factory.ConfigurationOverrides["Ingest:GitHubRepoAllowlist:0"] = "fix-portal/example";
        }

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetServices<IUsageSource>().Should().NotContain(source => source.SourceId == sourceId);
        scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Should()
            .ContainSingle(definition => definition.SourceId == sourceId)
            .Which.IsConfigured.Should()
            .BeFalse();
    }

    [Fact]
    public async Task RegistersExactlyOneDefinitionForEveryKnownSourceWhenUnconfigured()
    {
        await using var factory = new IngestFactory();

        var definitions = factory.Services.GetServices<SourceDefinition>().ToList();

        definitions
            .Select(x => x.SourceId)
            .Should()
            .BeEquivalentTo(
                UsageSourceIds.AnthropicUsageApi,
                UsageSourceIds.CopilotOrgReport,
                UsageSourceIds.GoogleCloudBillingExport,
                UsageSourceIds.OpenAiUsageApi,
                UsageSourceIds.OpenAiCostsApi,
                UsageSourceIds.GitHubActivityApi
            );
        definitions.Should().OnlyContain(x => !x.IsConfigured);
        definitions.Select(x => x.SourceId).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("ANTHROPIC_BILLING_KEY", typeof(AnthropicIngestionService), UsageSourceIds.AnthropicUsageApi)]
    [InlineData("COPILOT_ORG", typeof(CopilotIngestionService), UsageSourceIds.CopilotOrgReport)]
    [InlineData("GOOGLE_BILLING_ACCOUNT_ID", typeof(GoogleIngestionService), UsageSourceIds.GoogleCloudBillingExport)]
    [InlineData("GITHUB_TOKEN", typeof(GitHubIngestionService), UsageSourceIds.GitHubActivityApi)]
    public async Task ConfiguredCredentialRegistersTheMatchingUsageSource(
        string setting,
        Type implementationType,
        string sourceId
    )
    {
        await using var factory = new IngestFactory();
        factory.Settings[setting] = "configured";
        if (setting == "COPILOT_ORG")
        {
            factory.Settings["GITHUB_TOKEN"] = "configured-token";
        }
        if (setting == "GITHUB_TOKEN")
        {
            factory.ConfigurationOverrides["Ingest:GitHubRepoAllowlist:0"] = "fix-portal/example";
        }

        using var scope = factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetServices<IUsageSource>().ToList();
        var definitions = scope.ServiceProvider.GetServices<SourceDefinition>().ToList();

        sources.Should().ContainSingle(x => x.GetType() == implementationType).Which.SourceId.Should().Be(sourceId);
        definitions.Should().ContainSingle(x => x.SourceId == sourceId).Which.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task OpenAiAdminKeyRegistersTwoIndependentSourcesWithOneClientAndTheBillingWriter()
    {
        await using var factory = new IngestFactory();
        factory.Settings["OPENAI_ADMIN_KEY"] = "configured-secret";

        using var scope = factory.Services.CreateScope();
        var openAiSources = scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Where(source => source.SourceId is UsageSourceIds.OpenAiUsageApi or UsageSourceIds.OpenAiCostsApi)
            .ToList();
        var definitions = scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Where(definition => definition.SourceId is UsageSourceIds.OpenAiUsageApi or UsageSourceIds.OpenAiCostsApi)
            .ToList();

        openAiSources
            .Select(source => source.GetType())
            .Should()
            .BeEquivalentTo([typeof(OpenAiUsageSource), typeof(OpenAiCostsSource)]);
        definitions.Should().HaveCount(2).And.OnlyContain(definition => definition.IsConfigured);
        scope.ServiceProvider.GetServices<IOpenAiAdminClient>().Should().ContainSingle();
        scope.ServiceProvider.GetRequiredService<FxRateProvider>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<BillingObservationWriter>().Should().NotBeNull();
        definitions
            .Select(definition => definition.ToString())
            .Should()
            .OnlyContain(value => !value.Contains("configured-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnthropicUsageHttpClientOptsIntoFastModeForSpeedGrouping()
    {
        await using var factory = new IngestFactory();
        factory.Settings["ANTHROPIC_BILLING_KEY"] = "configured";

        using var client = factory
            .Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IAnthropicUsageClient));

        client.DefaultRequestHeaders.TryGetValues("anthropic-beta", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("fast-mode-2026-02-01");
    }

    [Fact]
    public async Task RegistersExactlyOneDailyDefinitionAndSourceForEachPublicPricingDocument()
    {
        await using var factory = new IngestFactory();
        using var scope = factory.Services.CreateScope();

        var sources = scope.ServiceProvider.GetServices<IPricingSource>().ToList();
        var definitions = scope.ServiceProvider.GetServices<PricingSourceDefinition>().ToList();

        sources
            .Select(source => source.SourceId)
            .Should()
            .BeEquivalentTo(PricingSourceIds.OpenAi, PricingSourceIds.Claude, PricingSourceIds.Kimi);
        sources.Select(source => source.SourceId).Should().OnlyHaveUniqueItems();
        definitions
            .Select(definition => definition.SourceId)
            .Should()
            .BeEquivalentTo(
                PricingSourceIds.OpenAi,
                PricingSourceIds.Claude,
                PricingSourceIds.Kimi,
                PricingSourceIds.GoogleCloudCatalog
            );
        definitions.Select(definition => definition.SourceId).Should().OnlyHaveUniqueItems();
        definitions
            .Should()
            .OnlyContain(definition => definition.ExpectedRefreshInterval == NodaTime.Duration.FromDays(1));
        definitions
            .Single(definition => definition.SourceId == PricingSourceIds.GoogleCloudCatalog)
            .IsConfigured.Should()
            .BeFalse();
    }

    [Fact]
    public async Task DoesNotRegisterGooglePricingWithoutVerifiedMappingsEvenWhenCredentialsExist()
    {
        await using var factory = new IngestFactory();
        factory.Settings["GOOGLE_CLOUD_CATALOG_API_KEY"] = "configured-key";
        factory.Settings["GOOGLE_CLOUD_CATALOG_SERVICE_ID"] = "configured-service";
        using var scope = factory.Services.CreateScope();

        scope
            .ServiceProvider.GetServices<IPricingSource>()
            .Should()
            .NotContain(source => source is GooglePricingSource);
        scope
            .ServiceProvider.GetServices<PricingSourceDefinition>()
            .Should()
            .ContainSingle(definition => definition.SourceId == PricingSourceIds.GoogleCloudCatalog)
            .Which.IsConfigured.Should()
            .BeFalse();
    }

    private static bool ExceptionChainContains(Exception ex, string fragment)
    {
        if (ex.Message.Contains(fragment, StringComparison.Ordinal))
        {
            return true;
        }
        if (ex is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(e => ExceptionChainContains(e, fragment));
        }
        return ex.InnerException is not null && ExceptionChainContains(ex.InnerException, fragment);
    }

    private static Exception? CaptureServicesException(IngestFactory factory) =>
        Record.Exception(() => factory.Services);

    private sealed class IngestFactory : WebApplicationFactory<Program>
    {
        private static readonly string[] SettingNames =
        [
            "DB_CONNECTION",
            "ANTHROPIC_BILLING_KEY",
            "GITHUB_TOKEN",
            "COPILOT_ORG",
            "GOOGLE_BILLING_ACCOUNT_ID",
            "OPENAI_ADMIN_KEY",
            "APPLICATIONINSIGHTS_CONNECTION_STRING",
            "GOOGLE_CLOUD_CATALOG_API_KEY",
            "GOOGLE_CLOUD_CATALOG_SERVICE_ID",
        ];

        public string? DatabaseConnection { get; init; } = "Host=unused;Database=unused";
        public Dictionary<string, string?> Settings { get; } = [];
        public Dictionary<string, string?> ConfigurationOverrides { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(ConfigurationOverrides));
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var originals = SettingNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var name in SettingNames)
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
                Environment.SetEnvironmentVariable("DB_CONNECTION", DatabaseConnection);
                foreach (var setting in Settings)
                {
                    Environment.SetEnvironmentVariable(setting.Key, setting.Value);
                }

                return base.CreateHost(builder);
            }
            finally
            {
                foreach (var original in originals)
                {
                    Environment.SetEnvironmentVariable(original.Key, original.Value);
                }
            }
        }
    }
}
