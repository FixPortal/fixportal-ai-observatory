using System.Net;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class GooglePricingSourceTests
{
    private const string ApiKey = "synthetic-secret-key";
    private const string ServiceId = "synthetic-service";
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 25, 12, 0);
    private static readonly LocalDate UsageDate = new(2026, 8, 25);
    private static readonly GoogleSkuMapping[] Mappings =
    [
        new(
            "sku-input-us",
            "Gemini Enterprise Agent Platform",
            ["Gemini Enterprise Agent Platform"],
            "us",
            "text",
            "standard",
            "none",
            128_000,
            "token",
            0m
        ),
        new(
            "sku-output-global",
            "Gemini Enterprise Agent Platform",
            ["Gemini Enterprise Agent Platform"],
            "global",
            "text-output",
            "standard",
            "none",
            128_000,
            "token",
            0m
        ),
    ];

    [Fact]
    public async Task FetchReadsEveryPageAndRetainsOnlyExactMappedDimensions()
    {
        var handler = Pages(Page1(), Page2());
        var logger = new CapturingLogger();
        var source = Source(handler, logger);

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);

        candidate.Should().NotBeNull();
        candidate!.Provider.Should().Be(Provider.Google);
        candidate.SourceId.Should().Be(PricingSourceIds.GoogleCloudCatalog);
        candidate.SourceUrl.Should().Be("https://cloudbilling.googleapis.com/v1/services/synthetic-service/skus");
        candidate.RawEvidence.Should().NotContain(ApiKey).And.NotContain("pageToken").And.NotContain("sku-unmapped");

        var catalog = PricingCatalogJson.Deserialize<GooglePriceCatalog>(candidate.NormalizedCatalog);
        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Entries.Should().HaveCount(2);
        catalog
            .Resolve(
                "Gemini Enterprise Agent Platform",
                "sku-input-us",
                "us",
                "text",
                "standard",
                "none",
                128_000,
                UsageDate
            )
            .Should()
            .NotBeNull();
        catalog
            .Resolve(
                "Gemini Enterprise Agent Platform",
                "sku-input-us",
                "europe",
                "text",
                "standard",
                "none",
                128_000,
                UsageDate
            )
            .Should()
            .BeNull();

        handler.Requests.Should().HaveCount(2);
        handler
            .Requests.Should()
            .OnlyContain(request => request.Headers.GetValues("X-Goog-Api-Key").Single() == ApiKey);
        handler
            .Requests.Should()
            .OnlyContain(request => !request.RequestUri!.Query.Contains("key=", StringComparison.OrdinalIgnoreCase));
        handler.Requests[1].RequestUri!.Query.Should().Be("?pageToken=page%20two%2F%2B%3F");
        logger
            .Messages.Should()
            .ContainSingle()
            .Which.Should()
            .Be("Google catalog fetched: 2 mapped SKU(s), 1 unknown SKU(s).");
    }

    [Fact]
    public async Task FetchPreservesTheExactMappedExpressionAndConvertsNanosWithoutFloatingPoint()
    {
        var source = Source(Pages(Page1(), Page2()));

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);
        var entry = PricingCatalogJson
            .Deserialize<GooglePriceCatalog>(candidate!.NormalizedCatalog)
            .Entries.Single(x => x.SkuId == "sku-input-us");

        entry.Description.Should().Be("Synthetic Agent Platform text input tokens in US");
        entry
            .EffectiveTime.Should()
            .Be(Instant.FromUtc(2026, 8, 20, 12, 34, 56) + Duration.FromNanoseconds(123_456_789));
        entry.EffectiveDateIsProviderDeclared.Should().BeTrue();
        entry.GeoTaxonomyType.Should().Be("REGIONAL");
        entry.ServiceRegions.Should().Equal("us");
        entry.PricingUnit.Should().Be("token");
        entry.PricingUnitDescription.Should().Be("token");
        entry.BaseUnit.Should().Be("token");
        entry.BaseUnitConversionFactor.Should().Be(1m);
        entry.DisplayQuantity.Should().Be(1_000_000m);
        entry.TierStartUsageAmount.Should().Be(0m);
        entry.UnitPriceUnits.Should().Be(0);
        entry.UnitPriceNanos.Should().Be(1250);
        entry.AggregationLevel.Should().Be("PROJECT");
        entry.AggregationInterval.Should().Be("DAILY");
        entry.AggregationCount.Should().Be(1);
        entry.Rate.Should().Be(1.25m);
    }

    [Fact]
    public async Task FetchRejectsTheWholeCandidateWhenALaterPageFailsWithoutLeakingResponseOrRequestData()
    {
        var handler = new RecordingHandler(
            (request, call) =>
                call == 1
                    ? Json(Page1())
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent($"do not leak {ApiKey} or {request.RequestUri!.Query}"),
                    }
        );
        var source = Source(handler);

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.Message.Should().NotContain(ApiKey).And.NotContain("pageToken").And.NotContain("do not leak");
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("ambiguous-tier")]
    [InlineData("usage-unit")]
    [InlineData("missing-pricing")]
    public async Task FetchRejectsMappedSkusWithUnrecognizedPricingExpressions(string mutation)
    {
        var firstPage = Mutate(Page1(), mutation);
        var source = Source(Pages(firstPage, Page2()));

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task FetchRejectsRepeatedPaginationTokens()
    {
        var secondPage = Page2().Replace("\n}", ",\n  \"nextPageToken\": \"page two/+?\"\n}", StringComparison.Ordinal);
        var source = Source(Pages(Page1(), secondPage));

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public void BundledCatalogIsValidAndContainsNoUnverifiedRates()
    {
        var catalog = PricingCatalogJson.Deserialize<GooglePriceCatalog>(Bundle());

        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Currency.Should().Be("USD");
        catalog
            .SourceUrl.Should()
            .Be("https://cloud.google.com/gemini-enterprise-agent-platform/generative-ai/pricing");
        catalog.RetrievedAt.Should().Be(Instant.FromUtc(2026, 8, 25, 0, 0));
        catalog.Entries.Should().BeEmpty();
    }

    private static GooglePricingSource Source(RecordingHandler handler, CapturingLogger? logger = null) =>
        new(
            new FakeClock(RetrievedAt),
            logger ?? new CapturingLogger(),
            Options.Create(
                new IngestOptions { GoogleCloudCatalogApiKey = ApiKey, GoogleCloudCatalogServiceId = ServiceId }
            ),
            Mappings,
            handler
        );

    private static RecordingHandler Pages(params string[] pages) =>
        new((_, call) => call <= pages.Length ? Json(pages[call - 1]) : throw new InvalidOperationException());

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };

    private static string Mutate(string json, string mutation) =>
        mutation switch
        {
            "currency" => json.Replace(
                "\"currencyCode\": \"USD\"",
                "\"currencyCode\": \"EUR\"",
                StringComparison.Ordinal
            ),
            "ambiguous-tier" => json.Replace(
                "\"tieredRates\": [",
                "\"tieredRates\": [{\"startUsageAmount\": 1, \"unitPrice\": {\"currencyCode\": \"USD\", \"units\": \"0\", \"nanos\": 1}},",
                StringComparison.Ordinal
            ),
            "usage-unit" => json.Replace(
                "\"usageUnit\": \"token\"",
                "\"usageUnit\": \"character\"",
                StringComparison.Ordinal
            ),
            "missing-pricing" => json.Replace(
                "\"pricingInfo\": [",
                "\"pricingInfoMissing\": [",
                StringComparison.Ordinal
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static string Page1() => Fixture("google-skus-page-1.json");

    private static string Page2() => Fixture("google-skus-page-2.json");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", name));

    private static string Bundle() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "google.json"));

    internal sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        private int _calls;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(response(request, Interlocked.Increment(ref _calls)));
        }
    }

    private sealed class CapturingLogger : ILogger<GooglePricingSource>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }
}
