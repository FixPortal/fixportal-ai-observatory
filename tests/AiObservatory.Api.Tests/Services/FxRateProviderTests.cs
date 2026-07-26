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
    private static FxRateProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<FxRateProvider>.Instance);

    [Fact]
    public async Task GbpShortCircuitsToOneAndMakesNoRequest()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("GBP", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        rate.Should().Be(1m);
        handler.Requested.Should().BeEmpty("GBP needs no conversion, so it must not cost a network call");
    }

    [Fact]
    public async Task UsesTheDatedEndpointForTheChargeDate()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("USD", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        rate.Should().Be(0.7412m);
        handler.Requested.Should().ContainSingle()
            .Which.Should().Contain("/v1/2026-03-15").And.Contain("from=USD");
    }

    [Fact]
    public async Task CachesPerDateSoTheSameDayIsFetchedOnce()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);
        var date = new LocalDate(2026, 3, 15);

        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);
        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);

        handler.Requested.Should().ContainSingle("a historical rate is immutable, so it caches indefinitely");
    }

    [Fact]
    public async Task FallsBackRatherThanFailingTheWrite()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync("USD", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        // 0.79m is FxRateProvider's private Fallback constant, documented as a recent
        // USD->GBP rate. USD is the only currency phase-1 entries offer besides GBP, so it
        // is the only one with a static fallback -- see ThrowsForANonUsdCurrencyOnOutage.
        rate.Should().Be(0.79m, "an FX outage must not block recording a real USD charge");
    }

    [Fact]
    public async Task ThrowsForANonUsdCurrencyOnOutage()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);
        var date = new LocalDate(2026, 3, 15);

        var act = () => sut.GetGbpRateOnAsync("EUR", date, TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<FxUnavailableException>(
            "freezing a USD-shaped fallback rate against a EUR charge would be undetectably wrong");
        ex.Which.Message.Should().Contain("EUR").And.Contain("2026-03-15");
    }
}
