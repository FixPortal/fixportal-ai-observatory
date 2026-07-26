using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

public class SpendEntriesEndpointsWafTests(AiObservatoryApiFactory factory)
    : IClassFixture<AiObservatoryApiFactory>
{
    /// <summary>Creates a category and a vendor and returns their ids.</summary>
    private static async Task<(Guid CategoryId, Guid VendorId)> SeedCatalogAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ct = TestContext.Current.CancellationToken;

        var cat = await client.PostAsJsonAsync("/api/spend/categories",
            new { Key = $"credits-{suffix}", DisplayName = "Credits", ColorVar = "--c", SortOrder = 1 }, ct);
        var categoryId = (await cat.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var ven = await client.PostAsJsonAsync("/api/spend/vendors",
            new { Key = $"anthropic-{suffix}", DisplayName = "Anthropic", Provider = "anthropic" }, ct);
        var vendorId = (await ven.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        return (categoryId, vendorId);
    }

    private static object Entry(Guid categoryId, Guid vendorId, string? entryKey, decimal amount = 80m,
        string currency = "GBP", string source = "Csv") =>
        new
        {
            OccurredOn = "2026-07-12",
            VendorId = vendorId,
            CategoryId = categoryId,
            Amount = amount,
            Currency = currency,
            Description = "Top-up",
            Source = source,
            EntryKey = entryKey,
        };

    /// <summary>
    /// The single most important test here. Re-posting an identical payload must land
    /// nothing and leave the total untouched — the failure this project has been burned by.
    /// </summary>
    [Fact]
    public async Task RePostingTheSamePayload_LandsNothingAndLeavesTheTotalUnchanged()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var key = $"k-{Guid.NewGuid():N}";
        var payload = new[] { Entry(categoryId, vendorId, key) };

        var first = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Single().GetProperty("status").GetString().Should().Be("created");

        var totalAfterFirst = await TotalAsync(client, vendorId);

        var second = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        (await second.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Single().GetProperty("status").GetString().Should().Be("duplicate");

        (await TotalAsync(client, vendorId)).Should().Be(totalAfterFirst, "a duplicate must not move the total");
    }

    [Fact]
    public async Task MixedBatch_ReturnsPerRowVerdictsAndLandsOnlyTheGoodRow()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var existingKey = $"k-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/api/spend/entries",
            new[] { Entry(categoryId, vendorId, existingKey) }, ct);

        var mixed = new object[]
        {
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}"),          // good
            Entry(categoryId, vendorId, existingKey),                       // duplicate
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", -5m),      // rejected: negative
        };

        var response = await client.PostAsJsonAsync("/api/spend/entries", mixed, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var statuses = (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Select(r => r.GetProperty("status").GetString()).ToArray();

        statuses.Should().Equal("created", "duplicate", "rejected");
    }

    [Fact]
    public async Task ManualEntriesAreNeverDeduplicated()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var payload = new[] { Entry(categoryId, vendorId, entryKey: null, source: "Manual") };

        await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        var second = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);

        (await second.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Single().GetProperty("status").GetString()
            .Should().Be("created", "a person typing the same charge twice is a mistake to show, not silence");
    }

    [Fact]
    public async Task GbpEntryStoresRateOneAndTheSameGbpAmount()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        await client.PostAsJsonAsync("/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 80m, "GBP") }, ct);

        var entries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/entries?vendorId={vendorId}", ct);
        var entry = entries.EnumerateArray().Single();

        entry.GetProperty("fxRate").GetDecimal().Should().Be(1m);
        entry.GetProperty("amountGbp").GetDecimal().Should().Be(80m);
    }

    [Fact]
    public async Task UnknownVendorIsRejectedRatherThanCreated()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, _) = await SeedCatalogAsync(client);

        var response = await client.PostAsJsonAsync("/api/spend/entries",
            new[] { Entry(categoryId, Guid.NewGuid(), $"k-{Guid.NewGuid():N}") }, ct);

        (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Single().GetProperty("status").GetString().Should().Be("rejected");
    }

    /// <summary>
    /// After Task 2's review, GetGbpRateOnAsync throws FxUnavailableException for any
    /// currency other than GBP (short-circuit) or USD (static fallback) when the rate
    /// cannot be resolved. A currency this endpoint has never been told to expect a rate
    /// for is the cleanest way to reach that branch without faking the FX HTTP call.
    /// </summary>
    [Fact]
    public async Task AnEntryWhoseFxCannotBeResolvedIsRejectedRatherThanFrozen()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        var response = await client.PostAsJsonAsync("/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 80m, "XTS") }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Single();
        result.GetProperty("status").GetString().Should().Be("rejected");
        result.GetProperty("reason").GetString().Should().Contain("XTS");
    }

    private static async Task<decimal> TotalAsync(HttpClient client, Guid vendorId)
    {
        var entries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/entries?vendorId={vendorId}", TestContext.Current.CancellationToken);
        return entries.EnumerateArray().Sum(e => e.GetProperty("amountGbp").GetDecimal());
    }
}
