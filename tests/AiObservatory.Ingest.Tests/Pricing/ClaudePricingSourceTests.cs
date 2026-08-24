using System.Net;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class ClaudePricingSourceTests
{
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 24, 12, 0);
    private static readonly LocalDate ObservedOn = new(2026, 8, 24);

    [Fact]
    public void ParserPreservesCacheDurationsBatchFastAndStatedGeography()
    {
        var catalog = ClaudePricingSource.Parse(Fixture(), RetrievedAt);

        var opus = catalog.Resolve("claude-opus-5-20260801", ObservedOn);
        var older = catalog.Resolve("claude-opus-4-5-20251101", ObservedOn);

        opus.Should().NotBeNull();
        opus!.Input.Should().Be(5m);
        opus.Output.Should().Be(25m);
        opus.CacheRead.Should().Be(0.50m);
        opus.CacheWrite5m.Should().Be(6.25m);
        opus.CacheWrite1h.Should().Be(10m);
        opus.BatchInput.Should().Be(2.50m);
        opus.BatchOutput.Should().Be(12.50m);
        opus.FastInput.Should().Be(10m);
        opus.FastOutput.Should().Be(50m);
        opus.UsInferenceMultiplier.Should().Be(1.1m);
        older!.FastInput.Should().BeNull();
        older.FastOutput.Should().BeNull();
        older.UsInferenceMultiplier.Should().BeNull();
    }

    [Fact]
    public void ParserKeepsSonnetFiveAtItsDeclaredCurrentStandardPriceWithoutInventingAnIncrease()
    {
        var catalog = ClaudePricingSource.Parse(Fixture(), RetrievedAt);

        var sonnetEntries = catalog.Entries.Where(entry => entry.ModelPrefix == "claude-sonnet-5").ToList();

        sonnetEntries.Should().ContainSingle();
        sonnetEntries[0].Input.Should().Be(2m);
        sonnetEntries[0].Output.Should().Be(10m);
        sonnetEntries[0].EffectiveFrom.Should().Be(ObservedOn);
        sonnetEntries[0].EffectiveDateIsProviderDeclared.Should().BeFalse();
    }

    [Theory]
    [InlineData("missing-heading")]
    [InlineData("duplicate-key")]
    [InlineData("overlapping-normalized-model")]
    [InlineData("partial-batch")]
    [InlineData("non-usd")]
    [InlineData("zero-rate")]
    [InlineData("negative-rate")]
    [InlineData("unknown-column")]
    public void ParserRejectsMalformedOrAmbiguousCatalogs(string mutation)
    {
        var act = () => ClaudePricingSource.Parse(Mutate(Fixture(), mutation), RetrievedAt);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task FetchProducesTheClaudeSourceContract()
    {
        var handler = new FirstPartyDocumentFetcherTests.RecordingHandler(
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture()) }
        );
        var source = new ClaudePricingSource(new FakeClock(RetrievedAt), handler);

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);

        candidate!.Provider.Should().Be(Provider.Anthropic);
        candidate.SourceId.Should().Be(PricingSourceIds.Claude);
        candidate.SourceUrl.Should().Be("https://platform.claude.com/docs/en/about-claude/pricing.md");
        var catalog = PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(candidate.NormalizedCatalog);
        ((Action)catalog.Validate).Should().NotThrow();
    }

    [Fact]
    public void BundledCatalogHasNoExcludedSonnetIncrease()
    {
        var catalog = PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(Bundle("claude.json"));

        ((Action)catalog.Validate).Should().NotThrow();
        catalog
            .Should()
            .BeEquivalentTo(ClaudePricingSource.Parse(Fixture(), RetrievedAt), options => options.WithStrictOrdering());
        catalog.SourceUrl.Should().Be("https://platform.claude.com/docs/en/about-claude/pricing.md");
        catalog.RetrievedAt.Should().Be(RetrievedAt);
        catalog
            .Entries.Where(entry => entry.ModelPrefix == "claude-sonnet-5")
            .Should()
            .ContainSingle()
            .Which.Input.Should()
            .Be(2m);
    }

    private static string Mutate(string document, string mutation)
    {
        const string opusRow = "| Claude Opus 5 | $5 / MTok | $6.25 / MTok | $10 / MTok | $0.50 / MTok | $25 / MTok |";
        const string opusAliasRow =
            "| Claude Opus 5 ([current](https://example.test)) | $5 / MTok | $6.25 / MTok | $10 / MTok | $0.50 / MTok | $25 / MTok |";
        const string sonnetBatch = "| Claude Sonnet 5 | $1 / MTok | $5 / MTok |";
        return mutation switch
        {
            "missing-heading" => document.Replace("### Fast mode pricing", "### Fast rates", StringComparison.Ordinal),
            "duplicate-key" => document.Replace(opusRow, $"{opusRow}\n{opusRow}", StringComparison.Ordinal),
            "overlapping-normalized-model" => document.Replace(
                opusRow,
                $"{opusRow}\n{opusAliasRow}",
                StringComparison.Ordinal
            ),
            "partial-batch" => document.Replace(sonnetBatch, "", StringComparison.Ordinal),
            "non-usd" => document.Replace("All prices are in USD.", "All prices are in EUR.", StringComparison.Ordinal),
            "zero-rate" => document.Replace("$25 / MTok", "$0 / MTok", StringComparison.Ordinal),
            "negative-rate" => document.Replace("$25 / MTok", "$-25 / MTok", StringComparison.Ordinal),
            "unknown-column" => document.Replace(
                "| Model | Base Input Tokens |",
                "| Model | Currency | Base Input Tokens |",
                StringComparison.Ordinal
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static string Fixture() => ReadFixture("claude-pricing.md");

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", name));

    private static string Bundle(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", name));
}
