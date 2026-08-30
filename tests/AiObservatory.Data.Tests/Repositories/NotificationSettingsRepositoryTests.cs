using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

[Trait("Category", "Integration")]
public class NotificationSettingsRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IUsageRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = $"aiobs_test_notification_settings_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new UsageRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ctx is not null && _connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
            {
                await _ctx.Database.EnsureDeletedAsync();
            }
        }
        finally
        {
            if (_ctx is not null)
            {
                await _ctx.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task GetNotificationSettings_returns_null_when_no_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _repo.GetNotificationSettingsAsync(ct)).Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationSettings_returns_the_singleton_row()
    {
        var ct = TestContext.Current.CancellationToken;
        _ctx.NotificationSettings.Add(
            new NotificationSettings
            {
                AlertEmailTo = "alerts@example.com",
                SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
            }
        );
        await _ctx.SaveChangesAsync(ct);

        var settings = await _repo.GetNotificationSettingsAsync(ct);

        settings.Should().NotBeNull();
        settings!.AlertEmailTo.Should().Be("alerts@example.com");
        settings.SlackWebhookUrl.Should().Be("https://hooks.slack.com/services/T0/B0/xyz");
    }
}
