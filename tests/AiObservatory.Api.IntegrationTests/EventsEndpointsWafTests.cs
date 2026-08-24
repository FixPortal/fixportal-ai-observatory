using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

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
    private static object NewEventBody(string? eventKey = null, DateTimeOffset? occurredAtUtc = null) =>
        new
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
        var raw =
            """{"Provider":"anthropic","Model":"m","InputTokens":1,"OutputTokens":1,"CacheReadTokens":0,"CacheWriteTokens":0,"CostUsd":0.01,"RawPayload":"not json"}""";

        using var content = new StringContent(raw, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/events", content, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenRawPayloadHasDuplicateProperties_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            CostUsd = 0.01m,
            RawPayload = """{"request":"first","request":"last"}""",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenUnknownProvider_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var raw =
            """{"Provider":"not-a-real-provider","Model":"m","InputTokens":1,"OutputTokens":1,"CacheReadTokens":0,"CacheWriteTokens":0,"CostUsd":0.01,"RawPayload":"{}"}""";

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
        var body = NewEventBody(eventKey: key, occurredAtUtc: new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

        var first = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        json.GetProperty("corrected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PostEvent_WhenProvenanceIsOmitted_PersistsExactLegacyDefaults()
    {
        using var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync(
            "/api/events",
            NewEventBody(eventKey: $"waf-legacy-provenance-{Guid.NewGuid():N}"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("sourceId").GetString().Should().Be(UsageSourceIds.LegacyApi);
        stored.GetProperty("sourceKind").GetString().Should().Be("legacy");
        stored.GetProperty("usageScope").GetString().Should().Be("unknown");
        stored.GetProperty("costBasis").GetString().Should().Be("unknown");
        stored
            .GetProperty("observedAt")
            .GetDateTimeOffset()
            .Should()
            .Be(stored.GetProperty("ingestedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task PostEvent_WhenProvenanceIsExplicit_NormalizesAndRoundTripsIt()
    {
        using var client = factory.CreateAdminClient();
        var observedAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 10,
            OutputTokens = 2,
            CostUsd = 0.01m,
            RawPayload = "{}",
            EventKey = $"waf-explicit-provenance-{Guid.NewGuid():N}",
            SourceId = "  CoDeX-LoCaL  ",
            SourceKind = "lOcAlTeLeMeTrY",
            UsageScope = "SUBSCRIPTION",
            CostBasis = "nOtIoNaL",
            ObservedAtUtc = observedAt,
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("sourceId").GetString().Should().Be(UsageSourceIds.CodexLocal);
        stored.GetProperty("sourceKind").GetString().Should().Be("localTelemetry");
        stored.GetProperty("usageScope").GetString().Should().Be("subscription");
        stored.GetProperty("costBasis").GetString().Should().Be("notional");
        stored.GetProperty("observedAt").GetDateTimeOffset().Should().Be(observedAt);
    }

    [Theory]
    [InlineData("SourceKind", "not-a-kind")]
    [InlineData("UsageScope", "0")]
    [InlineData("CostBasis", "not-a-basis")]
    public async Task PostEvent_WhenAProvenanceEnumIsInvalid_ReturnsBadRequest(string field, string value)
    {
        using var client = factory.CreateAdminClient();
        var body = new Dictionary<string, object?>
        {
            ["Provider"] = "openai",
            ["Model"] = "gpt-5.4",
            ["InputTokens"] = 1,
            ["OutputTokens"] = 1,
            ["RawPayload"] = "{}",
            [field] = value,
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenSourceIdExceedsStorageBoundary_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            RawPayload = "{}",
            SourceId = new string('s', 101),
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenObservedAtIsInTheFuture_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var now = factory.Services.GetRequiredService<NodaTime.IClock>().GetCurrentInstant();
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            RawPayload = "{}",
            ObservedAtUtc = now.Plus(NodaTime.Duration.FromHours(1)).ToDateTimeOffset(),
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenSnapshotChanges_ReturnsCorrectedWithoutDuplicate()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-corrected-{Guid.NewGuid():N}";
        var first = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            RawPayload = "{}",
            EventKey = key,
            SourceId = UsageSourceIds.CodexLocal,
            SourceKind = "localTelemetry",
            UsageScope = "subscription",
            CostBasis = "notional",
        };
        var corrected = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 2,
            OutputTokens = 1,
            RawPayload = "{}",
            EventKey = key,
            SourceId = UsageSourceIds.CodexLocal,
            SourceKind = "localTelemetry",
            UsageScope = "subscription",
            CostBasis = "notional",
        };

        (await client.PostAsJsonAsync("/api/events", first, TestContext.Current.CancellationToken))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var response = await client.PostAsJsonAsync("/api/events", corrected, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("duplicate").GetBoolean().Should().BeFalse();
        json.GetProperty("corrected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PatchEventCost_WhenSourceIdIsSupplied_UpdatesOnlyThatSourceIdentity()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-source-patch-{Guid.NewGuid():N}";
        async Task<Guid> Post(string? sourceId)
        {
            var body = new
            {
                Provider = "openai",
                Model = "gpt-5.4",
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = 1m,
                RawPayload = "{}",
                EventKey = key,
                SourceId = sourceId,
            };
            var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            return (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
                .GetProperty("id")
                .GetGuid();
        }

        var legacyId = await Post(null);
        var localId = await Post(UsageSourceIds.CodexLocal);
        var patch = await client.PatchAsJsonAsync(
            $"/api/events/{key}/cost?provider=openai&sourceId={UsageSourceIds.CodexLocal}",
            new { CostUsd = 2m },
            TestContext.Current.CancellationToken
        );

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>($"/api/events/{legacyId}", TestContext.Current.CancellationToken))
            .GetProperty("costUsd")
            .GetDecimal()
            .Should()
            .Be(1m);
        (await client.GetFromJsonAsync<JsonElement>($"/api/events/{localId}", TestContext.Current.CancellationToken))
            .GetProperty("costUsd")
            .GetDecimal()
            .Should()
            .Be(2m);
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
            CostUsd = 999.99m, // deliberately absurd; must be ignored
            RawPayload = "{}",
            EventKey = key,
            OccurredAtUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored
            .GetProperty("costUsd")
            .GetDecimal()
            .Should()
            .Be(14.70m, "2.00 + 10.00 + 0.20 + 2.50 at one million tokens each");
    }

    /// <summary>
    /// The same event, now declaring its cache write as one-hour TTL, must cost more: 1h
    /// writes bill at 2x base input against the five-minute rate's 1.25x. This is the whole
    /// point of the split — a deployment writing exclusively one-hour entries was understated.
    /// </summary>
    [Fact]
    public async Task PostEvent_WhenCacheWriteIsOneHour_PricesAtTheOneHourRate()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-test-cost-1h-{Guid.NewGuid():N}";

        var body = new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-5",
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = 1_000_000,
            CacheWriteTokens = 1_000_000,
            CacheWrite1hTokens = 1_000_000, // the entire write is one-hour TTL
            CostUsd = 0m,
            RawPayload = "{}",
            EventKey = key,
            OccurredAtUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored
            .GetProperty("costUsd")
            .GetDecimal()
            .Should()
            .Be(16.20m, "2.00 + 10.00 + 0.20 + 4.00 — the 4.00 one-hour rate replaces the 2.50 five-minute one");
    }

    /// <summary>
    /// The one-hour count is a subset of the total, so an over-large one is a malformed
    /// request rather than something to silently clamp at the boundary.
    /// </summary>
    [Fact]
    public async Task PostEvent_WhenCacheWrite1hExceedsCacheWrite_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var body = new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-5",
            InputTokens = 0,
            OutputTokens = 0,
            CacheReadTokens = 0,
            CacheWriteTokens = 1_000,
            CacheWrite1hTokens = 1_001,
            CostUsd = 0m,
            RawPayload = "{}",
            EventKey = $"waf-test-cost-1h-bad-{Guid.NewGuid():N}",
            OccurredAtUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("costUsd").GetDecimal().Should().Be(42.5m);
    }

    [Fact]
    public async Task PostEvent_PersistsTelemetryIdentityThoughtsAndUnknownCost()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Runtime = "codex",
            SessionId = "session-42",
            AgentId = "main",
            Model = "gpt-5.4",
            InputTokens = 100,
            OutputTokens = 50,
            CacheReadTokens = 20,
            CacheWriteTokens = 10,
            CacheWrite1hTokens = 0,
            ThoughtTokens = 30,
            CostUsd = (decimal?)null,
            RawPayload = "{}",
            EventKey = "openai:codex:session-42:main:gpt-5.4:100:50:20:10:0:30",
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("runtime").GetString().Should().Be("codex");
        stored.GetProperty("sessionId").GetString().Should().Be("session-42");
        stored.GetProperty("agentId").GetString().Should().Be("main");
        stored.GetProperty("thoughtTokens").GetInt64().Should().Be(30);
        stored.GetProperty("costUsd").ValueKind.Should().Be(JsonValueKind.Null);

        var events = await client.GetFromJsonAsync<JsonElement>(
            "/api/events?provider=openai",
            TestContext.Current.CancellationToken
        );
        var listed = events.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        listed.GetProperty("runtime").GetString().Should().Be("codex");
        listed.GetProperty("sessionId").GetString().Should().Be("session-42");
        listed.GetProperty("agentId").GetString().Should().Be("main");
        listed.GetProperty("thoughtTokens").GetInt64().Should().Be(30);
        listed.GetProperty("costUsd").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PostEvent_WhenThoughtTokensAreNegative_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Runtime = "codex",
            SessionId = "session-42",
            AgentId = "main",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            ThoughtTokens = -1,
            CostUsd = 0.01m,
            RawPayload = "{}",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(100, HttpStatusCode.Created)]
    [InlineData(101, HttpStatusCode.BadRequest)]
    public async Task PostEvent_EnforcesTheRuntimeStorageBoundary(int runtimeLength, HttpStatusCode expectedStatus)
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Runtime = new string('r', runtimeLength),
            SessionId = "session-42",
            AgentId = "main",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = (long?)null,
            CacheWriteTokens = (long?)null,
            CacheWrite1hTokens = (long?)null,
            ThoughtTokens = (long?)null,
            CostUsd = 0.01m,
            RawPayload = "{}",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task PostEvent_RejectsOneHourCacheWriteWithoutATotalCacheWrite()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-5",
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = (long?)null,
            CacheWriteTokens = (long?)null,
            CacheWrite1hTokens = 1L,
            CostUsd = 0m,
            RawPayload = "{}",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetLocalSnapshots_ReturnsOnlyTheExactSourceWithoutRawPayload()
    {
        using var client = factory.CreateAdminClient();
        var key = $"codex:2026-08-24:gpt-5.4:{Guid.NewGuid():N}";
        var occurredAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        static object Snapshot(
            string sourceId,
            string eventKey,
            DateTimeOffset occurredAtUtc,
            string sourceKind = "localTelemetry"
        ) =>
            new
            {
                Provider = "openai",
                Model = "gpt-5.4",
                InputTokens = 30,
                OutputTokens = 5,
                CacheReadTokens = 3,
                CacheWriteTokens = 2,
                CacheWrite1hTokens = 1,
                ThoughtTokens = 4,
                CostUsd = 0.01m,
                RawPayload = "{\"private\":\"transcript evidence\"}",
                EventKey = eventKey,
                OccurredAtUtc = occurredAtUtc,
                Runtime = "codex",
                SourceId = sourceId,
                SourceKind = sourceKind,
                UsageScope = "subscription",
                CostBasis = "notional",
            };

        (
            await client.PostAsJsonAsync(
                "/api/events",
                Snapshot(UsageSourceIds.CodexLocal, key, occurredAt),
                TestContext.Current.CancellationToken
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        (
            await client.PostAsJsonAsync(
                "/api/events",
                Snapshot(UsageSourceIds.KimiLocal, $"kimi:{Guid.NewGuid():N}", occurredAt),
                TestContext.Current.CancellationToken
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var nonLocalKey = $"codex:legacy:{Guid.NewGuid():N}";
        (
            await client.PostAsJsonAsync(
                "/api/events",
                Snapshot(UsageSourceIds.CodexLocal, nonLocalKey, occurredAt, "legacy"),
                TestContext.Current.CancellationToken
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var tombstoneKey = $"codex:tombstone:{Guid.NewGuid():N}";
        (
            await client.PostAsJsonAsync(
                "/api/events",
                new
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    InputTokens = 0,
                    OutputTokens = 0,
                    CacheReadTokens = 0,
                    CacheWriteTokens = 0,
                    CacheWrite1hTokens = 0,
                    ThoughtTokens = 0,
                    CostUsd = 0m,
                    RawPayload = "{\"source\":\"observatory-sweep\",\"tool\":\"codex-cli\",\"tombstone\":true,\"reason\":\"removed\"}",
                    EventKey = tombstoneKey,
                    OccurredAtUtc = occurredAt,
                    Runtime = "codex-cli",
                    SourceId = UsageSourceIds.CodexLocal,
                    SourceKind = "localTelemetry",
                    UsageScope = "subscription",
                    CostBasis = "notional",
                },
                TestContext.Current.CancellationToken
            )
        ).StatusCode.Should().Be(HttpStatusCode.Created);
        var zeroKey = $"codex:zero:{Guid.NewGuid():N}";
        (
            await client.PostAsJsonAsync(
                "/api/events",
                new
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    InputTokens = 0,
                    OutputTokens = 0,
                    CacheReadTokens = 0,
                    CacheWriteTokens = 0,
                    CacheWrite1hTokens = 0,
                    ThoughtTokens = 0,
                    CostUsd = (decimal?)null,
                    RawPayload = "{\"source\":\"observatory-sweep\",\"tombstone\":false}",
                    EventKey = zeroKey,
                    OccurredAtUtc = occurredAt,
                    Runtime = "codex",
                    SourceId = UsageSourceIds.CodexLocal,
                    SourceKind = "localTelemetry",
                    UsageScope = "subscription",
                    CostBasis = "notional",
                },
                TestContext.Current.CancellationToken
            )
        ).StatusCode.Should().Be(HttpStatusCode.Created);
        var thoughtOnlyKey = $"codex:thought-only:{Guid.NewGuid():N}";
        (
            await client.PostAsJsonAsync(
                "/api/events",
                new
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    InputTokens = 0,
                    OutputTokens = 0,
                    CacheReadTokens = 0,
                    CacheWriteTokens = 0,
                    CacheWrite1hTokens = 0,
                    ThoughtTokens = 7,
                    CostUsd = (decimal?)null,
                    RawPayload = "{\"source\":\"thought-only\",\"tombstone\":true}",
                    EventKey = thoughtOnlyKey,
                    OccurredAtUtc = occurredAt,
                    Runtime = "codex",
                    SourceId = UsageSourceIds.CodexLocal,
                    SourceKind = "localTelemetry",
                    UsageScope = "subscription",
                    CostBasis = "notional",
                },
                TestContext.Current.CancellationToken
            )
        ).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await client.GetAsync(
            $"/api/events/local-snapshots?sourceId={UsageSourceIds.CodexLocal}",
            TestContext.Current.CancellationToken
        );
        var inventory = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = inventory.EnumerateArray().Single(x => x.GetProperty("eventKey").GetString() == key);
        item.GetProperty("provider").GetString().Should().Be("openai");
        item.GetProperty("sourceId").GetString().Should().Be(UsageSourceIds.CodexLocal);
        item.GetProperty("sourceKind").GetString().Should().Be("localTelemetry");
        item.GetProperty("occurredAtUtc").GetDateTimeOffset().Should().Be(occurredAt);
        item.GetProperty("costUsd").GetDecimal().Should().Be(0m);
        item.TryGetProperty("inputTokens", out _).Should().BeFalse();
        item.TryGetProperty("rawPayload", out _).Should().BeFalse();
        inventory
            .EnumerateArray()
            .Should()
            .NotContain(x => x.GetProperty("sourceId").GetString() == UsageSourceIds.KimiLocal);
        inventory.EnumerateArray().Should().NotContain(x => x.GetProperty("eventKey").GetString() == nonLocalKey);
        inventory.EnumerateArray().Should().NotContain(x => x.GetProperty("eventKey").GetString() == tombstoneKey);
        inventory.EnumerateArray().Should().Contain(x => x.GetProperty("eventKey").GetString() == zeroKey);
        inventory.EnumerateArray().Should().Contain(x => x.GetProperty("eventKey").GetString() == thoughtOnlyKey);
    }
}
