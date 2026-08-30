using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class NotificationSettingsEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Fact]
    public async Task Get_WhenNothingConfigured_ReturnsAllUnconfigured()
    {
        var ct = TestContext.Current.CancellationToken;

        // Singleton row, shared DB across every test in this class: guarantee a clean slate
        // rather than relying on execution order (xUnit does not promise declaration order).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await db.NotificationSettings.ExecuteDeleteAsync(ct);
        }

        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetFromJsonAsync<JsonElement>("/api/notification-settings", ct);

        response.GetProperty("emailConfigured").GetBoolean().Should().BeFalse();
        response.GetProperty("emailMasked").ValueKind.Should().Be(JsonValueKind.Null);
        response.GetProperty("slackConfigured").GetBoolean().Should().BeFalse();
        response.GetProperty("slackMasked").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Put_SetsEmailWithoutTouchingSlack_AndMasksTheResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { slackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz" },
            ct
        );

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "chris@fixportal.org" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("emailConfigured").GetBoolean().Should().BeTrue();
        body.GetProperty("emailMasked").GetString().Should().Be("ch***@fixportal.org");
        // The earlier PUT's Slack value must survive an edit that only touched email.
        body.GetProperty("slackConfigured").GetBoolean().Should().BeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var row = await db.NotificationSettings.SingleAsync(ct);
        row.AlertEmailTo.Should().Be("chris@fixportal.org");
        row.SlackWebhookUrl.Should().Be("https://hooks.slack.com/services/T0/B0/xyz");
    }

    [Fact]
    public async Task Put_WithNullEmail_ClearsItWithoutTouchingSlack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/api/notification-settings",
            new
            {
                alertEmailTo = "chris@fixportal.org",
                slackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
            },
            ct
        );

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = (string?)null },
            ct
        );

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("emailConfigured").GetBoolean().Should().BeFalse();
        body.GetProperty("slackConfigured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Put_WithMalformedEmail_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "not-an-email" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_WithNonSlackWebhookUrl_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { slackWebhookUrl = "https://evil.example.com/steal" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_WithNonStringAlertEmailTo_ReturnsBadRequestNotServerError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync("/api/notification-settings", new { alertEmailTo = 12345 }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task Put_WithNonObjectBody_ReturnsBadRequestNotServerError(string json)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsync(
            "/api/notification-settings",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_WithoutAdminKey_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateReadOnlyClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "chris@fixportal.org" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
