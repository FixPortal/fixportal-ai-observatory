using System.Net;
using AiObservatory.Api.Services.GitHub;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiObservatory.Api.Tests.Services;

public class GitHubBillingClientTests
{
    private static GitHubBillingClient Create(HttpClient http) =>
        new(http, "FixPortal", NullLogger<GitHubBillingClient>.Instance);

    private static HttpClient ClientFor(StubHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.github.com") };

    [Fact]
    public async Task ParsesTheBilledLinesFromAUsageResponse()
    {
        // grossAmount deliberately differs from netAmount here: with a zero discount the two
        // are equal, and the assertion below could not tell "reads netAmount" apart from
        // "reads grossAmount" if a later edit confused the two.
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {"usageItems":[
              {"date":"2026-07-01T00:00:00Z","product":"code_quality","sku":"Code Quality AI Credits",
               "quantity":1201.41527,"grossAmount":15.0,"discountAmount":2.9858473,"netAmount":12.0141527,
               "repositoryName":"fixportal-service-centerprise","unitType":"AICredits","pricePerUnit":0.01}
            ]}
            """
        );
        using var http = ClientFor(handler);

        var items = await Create(http).GetUsageAsync(2026, TestContext.Current.CancellationToken);

        items.Should().ContainSingle();
        items[0].Product.Should().Be("code_quality");
        items[0].Sku.Should().Be("Code Quality AI Credits");
        items[0]
            .NetAmount.Should()
            .Be(
                12.0141527m,
                "net is gross minus the discount — gross would double-count the included allowance"
            );
        handler
            .Requested.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("/organizations/FixPortal/settings/billing/usage")
            .And.Contain("year=2026");
    }

    /// <summary>
    /// The REST reference documents a date-only value; the live endpoint currently returns a
    /// full timestamp. Binding to either alone would break the day GitHub moved to the other,
    /// and the failure would be silent — the caller catches broadly, so a rejected payload
    /// reads as "no GitHub spend" rather than an error.
    /// </summary>
    [Theory]
    [InlineData("2026-07-01T00:00:00Z")]
    [InlineData("2026-07-01")]
    public async Task AcceptsBothTheDocumentedAndTheLiveDateFormat(string date)
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            $$"""
            {"usageItems":[
              {"date":"{{date}}","product":"actions","sku":"Actions Linux","netAmount":9.402}
            ]}
            """
        );
        using var http = ClientFor(handler);

        var items = await Create(http).GetUsageAsync(2026, TestContext.Current.CancellationToken);

        items.Should().ContainSingle();
        items[0].Date.Should().Be(new DateOnly(2026, 7, 1));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReturnsEmptyWhenTheTokenCannotSeeBilling(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(status, """{"message":"Forbidden"}""");
        using var http = ClientFor(handler);

        var items = await Create(http).GetUsageAsync(2026, TestContext.Current.CancellationToken);

        items
            .Should()
            .BeEmpty(
                "a token missing the billing scope must degrade to a visible gap, not take the "
                    + "worker's whole daily cycle down with it"
            );
    }

    [Fact]
    public async Task ThrowsOnAnUnexpectedFailureSoItIsNotMistakenForNoSpend()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "{}");
        using var http = ClientFor(handler);
        var client = Create(http);

        var act = () => client.GetUsageAsync(2026, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
