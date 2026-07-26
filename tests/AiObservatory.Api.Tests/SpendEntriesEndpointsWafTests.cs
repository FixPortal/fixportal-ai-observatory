using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Api.Services.Fx;
using AiObservatory.Api.Tests.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Posts a single-row batch and returns the created row's id.</summary>
    private static async Task<Guid> CreateEntryAsync(
        HttpClient client, Guid categoryId, Guid vendorId, decimal amount = 80m, string currency = "GBP")
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync("/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", amount, currency) }, ct);
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Single();
        return result.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Wires a stubbed primary HTTP handler onto FxRateProvider's typed client for one
    /// test's host, so FX scenarios (outage, unresolvable currency) are deterministic and
    /// never call out to the real frankfurter.dev. Caller owns disposal of the returned
    /// factory (it spins up its own TestServer, separate from the shared one).
    /// </summary>
    private static WebApplicationFactory<Program> WithStubbedFx(
        AiObservatoryApiFactory outer, StubHttpMessageHandler handler) =>
        outer.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient<FxRateProvider>().ConfigurePrimaryHttpMessageHandler(() => handler)));

    private static HttpClient AdminClient(WebApplicationFactory<Program> stubFactory)
    {
        var client = stubFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Observatory-Key", AiObservatoryApiFactory.AdminKey);
        return client;
    }

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

        // The status array alone doesn't prove landing was scoped to the one good row --
        // confirm the table itself: the pre-existing row plus exactly one new one.
        var entries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/entries?vendorId={vendorId}", ct);
        entries.EnumerateArray().Should().HaveCount(2,
            "only the pre-existing row and the one genuinely new row should have landed");
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
    /// cannot be resolved. Stubbed rather than a live call to frankfurter.dev: a real
    /// network failure and a genuine missing-rate response both land in the same catch, so
    /// a live call could pass this test for the wrong reason (e.g. a DNS blip in CI) rather
    /// than because the endpoint actually translates the exception correctly.
    /// </summary>
    [Fact]
    public async Task AnEntryWhoseFxCannotBeResolvedIsRejectedRatherThanFrozen()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        using var stubFactory = WithStubbedFx(factory, handler);
        using var client = AdminClient(stubFactory);
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        var batch = new object[]
        {
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 80m, "EUR"),   // unresolvable
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 50m, "GBP"),   // unaffected
        };

        var response = await client.PostAsJsonAsync("/api/spend/entries", batch, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToArray();

        results[0].GetProperty("status").GetString().Should().Be("rejected");
        results[0].GetProperty("reason").GetString().Should().Contain("EUR");

        // This is what the deviation actually exists to guarantee: one unresolvable
        // currency must not fail the batch -- the untroubled GBP row still lands.
        results[1].GetProperty("status").GetString().Should().Be("created",
            "an unresolvable EUR rate must not stop an unrelated GBP row in the same batch from landing");
    }

    [Fact]
    public async Task PatchWithUnknownVendorIdIsRejectedAndDoesNotMutateTheRow()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId);

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}",
            new { VendorId = Guid.NewGuid() }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        entries.EnumerateArray().Should().ContainSingle(e => e.GetProperty("id").GetGuid() == id,
            "a rejected patch must leave the original vendor link in place");
    }

    [Fact]
    public async Task PatchWithUnknownCategoryIdIsRejectedAndDoesNotMutateTheRow()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId);

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}",
            new { CategoryId = Guid.NewGuid() }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        entries.EnumerateArray().Should().ContainSingle(e => e.GetProperty("id").GetGuid() == id,
            "a rejected patch must leave the original category link in place");
    }

    [Fact]
    public async Task PatchAgainstANonExistentEntryIsNotFound()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{Guid.NewGuid()}",
            new { Description = "Anything" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchingAmountReResolvesAmountGbpAtTheStoredRate()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId, amount: 80m, currency: "GBP");

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}", new { Amount = 100m }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        entry.GetProperty("fxRate").GetDecimal().Should().Be(1m);
        entry.GetProperty("amountGbp").GetDecimal().Should().Be(100m,
            "changing the amount must re-resolve AmountGbp rather than leave the old conversion");
    }

    [Fact]
    public async Task PatchingOccurredOnReResolvesTheConversionForTheNewDate()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.8}}""");
        using var stubFactory = WithStubbedFx(factory, handler);
        using var client = AdminClient(stubFactory);
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        // USD, not GBP: GBP short-circuits before any FX lookup or cache entry, so it could
        // never prove a fresh lookup happened on the new date.
        var id = await CreateEntryAsync(client, categoryId, vendorId, amount: 80m, currency: "USD");

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}",
            new { OccurredOn = "2026-08-01" }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requested.Should().HaveCount(2,
            "one FX lookup for the original charge date at creation, a second for the patched date -- " +
            "a stale cached/frozen rate would mean only one request total");

        var entry = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var amount = entry.GetProperty("amount").GetDecimal();
        var fxRate = entry.GetProperty("fxRate").GetDecimal();
        entry.GetProperty("amountGbp").GetDecimal().Should().Be(decimal.Round(amount * fxRate, 4),
            "AmountGbp must stay consistent with whatever rate is actually stored, not a stale figure");
    }

    [Fact]
    public async Task PatchingOnlyDescriptionLeavesTheFrozenConversionUntouched()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId, amount: 80m, currency: "GBP");

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}",
            new { Description = "Renamed" }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        entry.GetProperty("description").GetString().Should().Be("Renamed");
        entry.GetProperty("fxRate").GetDecimal().Should().Be(1m);
        entry.GetProperty("amountGbp").GetDecimal().Should().Be(80m,
            "a description-only patch must not re-resolve FX -- that would defeat the freeze");
    }

    private static async Task<decimal> TotalAsync(HttpClient client, Guid vendorId)
    {
        var entries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/entries?vendorId={vendorId}", TestContext.Current.CancellationToken);
        return entries.EnumerateArray().Sum(e => e.GetProperty("amountGbp").GetDecimal());
    }
}
