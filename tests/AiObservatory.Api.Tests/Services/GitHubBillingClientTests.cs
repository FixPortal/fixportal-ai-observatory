using System.Net;
using AiObservatory.Api.Services.GitHub;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiObservatory.Api.Tests.Services;

public class GitHubBillingClientTests
{
    private static GitHubBillingClient Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") },
            "FixPortal", NullLogger<GitHubBillingClient>.Instance);

    [Fact]
    public async Task ParsesTheBilledLinesFromAUsageResponse()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """
        {"usageItems":[
          {"date":"2026-07-01T00:00:00Z","product":"code_quality","sku":"Code Quality AI Credits",
           "quantity":1201.41527,"grossAmount":12.0141527,"discountAmount":0,"netAmount":12.0141527,
           "repositoryName":"fixportal-service-centerprise","unitType":"AICredits","pricePerUnit":0.01}
        ]}
        """);

        var items = await Create(handler).GetUsageAsync(2026, TestContext.Current.CancellationToken);

        items.Should().ContainSingle();
        items[0].Product.Should().Be("code_quality");
        items[0].Sku.Should().Be("Code Quality AI Credits");
        items[0].NetAmount.Should().Be(12.0141527m,
            "net is gross minus the discount — gross would double-count the included allowance");
        handler.Requested.Should().ContainSingle()
            .Which.Should().Contain("/organizations/FixPortal/settings/billing/usage")
            .And.Contain("year=2026");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReturnsEmptyWhenTheTokenCannotSeeBilling(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(status, """{"message":"Forbidden"}""");

        var items = await Create(handler).GetUsageAsync(2026, TestContext.Current.CancellationToken);

        items.Should().BeEmpty(
            "a token missing the billing scope must degrade to a visible gap, not take the "
          + "worker's whole daily cycle down with it");
    }

    [Fact]
    public async Task ThrowsOnAnUnexpectedFailureSoItIsNotMistakenForNoSpend()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "{}");

        var act = () => Create(handler).GetUsageAsync(2026, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
