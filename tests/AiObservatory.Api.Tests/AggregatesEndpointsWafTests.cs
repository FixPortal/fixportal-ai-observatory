using System.Net;
using System.Net.Http.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.Tests;

/// <summary>
/// AIO-H3: GET /api/aggregates malformed-date and from&gt;to validation, plus the
/// LocalDate ISO-format regression guard (the fix for a real chart-axis-scrambling bug —
/// LocalDate.ToString() with no explicit pattern used the server culture's long-date format).
/// </summary>
[Collection("ApiFactory")]
public class AggregatesEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Theory]
    [InlineData("from", "not-a-date")]
    [InlineData("to", "2026-13-40")]
    public async Task GetAggregates_WhenDateMalformed_ReturnsBadRequest(string param, string value)
    {
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync($"/api/aggregates?{param}={value}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAggregates_WhenFromAfterTo_ReturnsBadRequest()
    {
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync(
            "/api/aggregates?from=2026-06-15&to=2026-06-01", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAggregates_ReturnsDatesInIsoFormat()
    {
        // Unique out-of-range window (year 2019) so this test's own row is unambiguously
        // identifiable regardless of what other tests in the shared collection have added.
        var date = new LocalDate(2019, 5, 29);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.DailyAggregates.Add(new DailyAggregate
            {
                Date = date,
                Provider = Provider.Anthropic,
                Model = "waf-iso-format-test",
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = 0.01m,
                RequestCount = 1,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateReadOnlyClient();
        var response = await client.GetAsync(
            "/api/aggregates?from=2019-05-29&to=2019-05-29", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var rows = await response.Content.ReadFromJsonAsync<List<AggregateRow>>(TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
        // Regression guard: must be strict yyyy-MM-dd, never the server culture's long-date
        // format ("29 May 2019") that broke the frontend's slice/sort and scrambled the axis.
        rows![0].Date.Should().Be("2019-05-29");
    }

    private sealed record AggregateRow(string Date);
}
