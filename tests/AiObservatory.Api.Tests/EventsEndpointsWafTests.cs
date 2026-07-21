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
}
