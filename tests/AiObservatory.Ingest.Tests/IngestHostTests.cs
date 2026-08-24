using AiObservatory.Data.Entities;
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
    [InlineData("ANTHROPIC_BILLING_KEY", typeof(AnthropicIngestionService))]
    [InlineData("COPILOT_ORG", typeof(CopilotIngestionService))]
    [InlineData("GOOGLE_BILLING_ACCOUNT_ID", typeof(GoogleIngestionService))]
    [InlineData("OPENAI_ADMIN_KEY", typeof(OpenAiIngestionService))]
    public async Task UnresolvedKeyVaultReferenceDoesNotEnableAProvider(string setting, Type serviceType)
    {
        await using var factory = new IngestFactory();
        factory.Settings[setting] = KeyVaultReference;
        if (setting == "COPILOT_ORG")
        {
            factory.Settings["GITHUB_TOKEN"] = "configured-token";
        }

        factory.Services.GetService(serviceType).Should().BeNull();
    }

    [Fact]
    public async Task StartupRejectsAnAnthropicRateWithoutCacheWrite1HPricing()
    {
        await using var factory = new IngestFactory();
        factory.Settings["ANTHROPIC_BILLING_KEY"] = "configured-key";
        factory.ConfigurationOverrides["Ingest:Anthropic:Pricing:0:CacheWrite1h"] = "0";

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown!, "CacheWrite1h").Should().BeTrue();
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
                UsageSourceIds.GitHubActivityApi
            );
        definitions.Should().OnlyContain(x => !x.IsConfigured);
        definitions.Select(x => x.SourceId).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("ANTHROPIC_BILLING_KEY", typeof(AnthropicIngestionService), UsageSourceIds.AnthropicUsageApi)]
    [InlineData("COPILOT_ORG", typeof(CopilotIngestionService), UsageSourceIds.CopilotOrgReport)]
    [InlineData("GOOGLE_BILLING_ACCOUNT_ID", typeof(GoogleIngestionService), UsageSourceIds.GoogleCloudBillingExport)]
    [InlineData("OPENAI_ADMIN_KEY", typeof(OpenAiIngestionService), UsageSourceIds.OpenAiUsageApi)]
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
        ];

        public string? DatabaseConnection { get; init; } = "Host=unused;Database=unused";
        public Dictionary<string, string?> Settings { get; } = [];
        public Dictionary<string, string?> ConfigurationOverrides { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(ConfigurationOverrides));
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
