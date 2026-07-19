using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

/// <summary>
/// AIO-H3: POST /api/budget-rules ThresholdUsd>0 guard. A zero/negative threshold would
/// fire a spurious alert (Insight row + email) every single evaluation cycle until deleted.
/// </summary>
[Collection("ApiFactory")]
public class BudgetRulesEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PostBudgetRule_WhenThresholdNotPositive_ReturnsBadRequest(decimal threshold)
    {
        using var client = factory.CreateAdminClient();
        var body = new { Provider = (string?)null, Period = "daily", ThresholdUsd = threshold };

        var response = await client.PostAsJsonAsync("/api/budget-rules", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostBudgetRule_WhenThresholdPositive_ReturnsCreated()
    {
        using var client = factory.CreateAdminClient();
        var body = new { Provider = (string?)null, Period = "weekly", ThresholdUsd = 25m };

        var response = await client.PostAsJsonAsync("/api/budget-rules", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
