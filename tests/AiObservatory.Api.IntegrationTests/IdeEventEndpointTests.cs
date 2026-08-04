using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

[Collection("ApiFactory")]
public sealed class IdeEventEndpointTests(AiObservatoryApiFactory factory)
{
    [Trait("Category", "Integration")]
    [Fact]
    public async Task RecordsAnIdenticalDeliveryOnceAndRejectsConflictingReuse()
    {
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing.decided.v1.json"),
            TestContext.Current.CancellationToken
        );
        using var client = factory.CreateIdeClient();

        var first = await client.PostAsync("/api/ide/v1/events", Json(bytes), TestContext.Current.CancellationToken);
        var second = await client.PostAsync("/api/ide/v1/events", Json(bytes), TestContext.Current.CancellationToken);
        var changed = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(bytes).Replace("assisted-v1", "assisted-v2", StringComparison.Ordinal)
        );
        var conflict = await client.PostAsync(
            "/api/ide/v1/events",
            Json(changed),
            TestContext.Current.CancellationToken
        );

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var rows = await db.IdeEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
        rows[0].EventType.Should().Be("routing.decided");
    }

    private static ByteArrayContent Json(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
