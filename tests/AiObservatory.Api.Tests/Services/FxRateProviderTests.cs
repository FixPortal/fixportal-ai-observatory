using System.Net;
using AiObservatory.Api.Services.Fx;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

/// <summary>
/// The ledger converts once, at write, using the rate on the CHARGE date — not "now".
/// Converting at render would make a historical charge show a different figure every day.
/// </summary>
public class FxRateProviderTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static FxRateProvider Create(StubHandler handler) =>
        new(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<FxRateProvider>.Instance);

    [Fact]
    public async Task GbpShortCircuitsToOneAndMakesNoRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("GBP", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        rate.Should().Be(1m);
        handler.Requested.Should().BeEmpty("GBP needs no conversion, so it must not cost a network call");
    }

    [Fact]
    public async Task UsesTheDatedEndpointForTheChargeDate()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("USD", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        rate.Should().Be(0.7412m);
        handler.Requested.Should().ContainSingle()
            .Which.Should().Contain("/v1/2026-03-15").And.Contain("from=USD");
    }

    [Fact]
    public async Task CachesPerDateSoTheSameDayIsFetchedOnce()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);
        var date = new LocalDate(2026, 3, 15);

        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);
        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);

        handler.Requested.Should().ContainSingle("a historical rate is immutable, so it caches indefinitely");
    }

    [Fact]
    public async Task FallsBackRatherThanFailingTheWrite()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("USD", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        rate.Should().BeGreaterThan(0m, "an FX outage must not block recording a real charge");
    }
}
