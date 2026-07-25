using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

/// <summary>
/// AIO-H3: POST /api/events validation branches (future-date, non-JSON RawPayload) and the
/// duplicate-vs-created status-code contract (200 OK+Duplicate for a repeated EventKey,
/// 201 Created for a genuinely new one). WebApplicationFactory end-to-end — these branches
/// live in the minimal-API handler itself, unreachable from a unit test.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class EventsEndpointsWafTests(AiObservatoryApiFactory factory)
{
    private static object NewEventBody(string? eventKey = null, DateTimeOffset? occurredAtUtc = null) => new
    {
        Provider = "anthropic",
        Model = "claude-sonnet-4-6",
        InputTokens = 100,
        OutputTokens = 50,
        CacheReadTokens = 0,
        CacheWriteTokens = 0,
        CostUsd = 0.01m,
        RawPayload = "{}",
        EventKey = eventKey,
        OccurredAtUtc = occurredAtUtc,
    };

    [Fact]
    public async Task PostEvent_WhenOccurredAtIsInTheFuture_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var body = NewEventBody(occurredAtUtc: DateTimeOffset.UtcNow.AddHours(1));

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenRawPayloadIsNotValidJson_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var raw = """{"Provider":"anthropic","Model":"m","InputTokens":1,"OutputTokens":1,"CacheReadTokens":0,"CacheWriteTokens":0,"CostUsd":0.01,"RawPayload":"not json"}""";

        using var content = new StringContent(raw, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/events", content, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenUnknownProvider_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var raw = """{"Provider":"not-a-real-provider","Model":"m","InputTokens":1,"OutputTokens":1,"CacheReadTokens":0,"CacheWriteTokens":0,"CostUsd":0.01,"RawPayload":"{}"}""";

        using var content = new StringContent(raw, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/events", content, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task PostEvent_WhenTokenCountsNegative_ReturnsBadRequest(long inputTokens, long outputTokens)
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "anthropic",
            Model = "m",
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0.01m,
            RawPayload = "{}",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenGenuinelyNew_ReturnsCreatedWithLocation()
    {
        using var client = factory.CreateAdminClient();
        var body = NewEventBody(eventKey: $"waf-test-new-{Guid.NewGuid():N}");

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task PostEvent_WhenEventKeyAlreadyRecorded_ReturnsOkWithDuplicateFlag()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-test-dup-{Guid.NewGuid():N}";
        var body = NewEventBody(eventKey: key);

        var first = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("duplicate").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// Anthropic events are priced server-side from the shared rate table, and a
    /// client-supplied CostUsd is discarded. Producers used to price their own events, which
    /// put a second rate table in every producer — the drift that made months of recorded
    /// spend wrong.
    /// </summary>
    [Fact]
    public async Task PostEvent_WhenAnthropic_PricesServerSideAndIgnoresSuppliedCost()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-test-cost-{Guid.NewGuid():N}";

        // One million of each token class on a date inside the Sonnet-5 introductory window
        // (2/10/0.20/2.50), so the expected cost is just the sum of the four rates.
        // The date must also be in the PAST — the handler rejects an OccurredAtUtc more than
        // five minutes ahead of now, so a fixed same-day timestamp fails whenever the suite
        // happens to run earlier in the day than the literal.
        var body = new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-5",
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = 1_000_000,
            CacheWriteTokens = 1_000_000,
            CostUsd = 999.99m,          // deliberately absurd; must be ignored
            RawPayload = "{}",
            EventKey = key,
            OccurredAtUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>($"/api/events/{id}", TestContext.Current.CancellationToken);

        stored.GetProperty("costUsd").GetDecimal().Should().Be(14.70m, "2.00 + 10.00 + 0.20 + 2.50 at one million tokens each");
    }

    /// <summary>
    /// The server-side override is Anthropic-only. Copilot and Moonshot are flat-rate
    /// subscriptions with no per-token price, and Google/OpenAI report billed figures, so
    /// their supplied cost has to survive untouched.
    /// </summary>
    [Fact]
    public async Task PostEvent_WhenNotAnthropic_KeepsSuppliedCost()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-test-cost-other-{Guid.NewGuid():N}";

        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 42.5m,
            RawPayload = "{}",
            EventKey = key,
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>($"/api/events/{id}", TestContext.Current.CancellationToken);

        stored.GetProperty("costUsd").GetDecimal().Should().Be(42.5m);
    }
}
