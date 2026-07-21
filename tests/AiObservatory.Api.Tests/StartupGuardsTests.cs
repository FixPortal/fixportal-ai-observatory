using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.Tests;

/// <summary>
/// Composition-root startup guards in Program.cs — each is a documented fix for a real
/// past incident (a guessable "change-me" admin key reachable outside dev; a missing
/// DB_CONNECTION failing silently at first request instead of at boot; the dev-only
/// /api/dev/seed route being reachable in Production). Every test here uses its own
/// throwaway factory (never the shared collection fixture) because it needs a
/// non-default Environment/ApiKeyOverride.
/// </summary>
public class StartupGuardsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("change-me")]
    public async Task Startup_WhenApiKeyIsUnsetOrPlaceholder_ThrowsOutsideDevelopment(string? apiKey)
    {
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Production,
            ApiKeyOverride = apiKey,
        };

        var thrown = Record.Exception(() => factory.Services);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown!, "OBSERVATORY_API_KEY").Should().BeTrue(
            $"the exception chain should mention OBSERVATORY_API_KEY; got: {thrown}");
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Startup_WhenApiKeyIsPlaceholder_SucceedsInDevelopment()
    {
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Development,
            ApiKeyOverride = "change-me",
        };

        var act = () => factory.Services;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Startup_WhenDbConnectionUnset_ThrowsRegardlessOfEnvironment()
    {
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Development };
        factory.SetDbConnection(null);

        var thrown = Record.Exception(() => factory.Services);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown!, "DB_CONNECTION").Should().BeTrue(
            $"the exception chain should mention DB_CONNECTION; got: {thrown}");
    }

    /// <summary>Walks Exception/InnerException (and AggregateException.InnerExceptions) looking
    /// for any message containing <paramref name="fragment"/> — the exact wrapper type
    /// HostFactoryResolver uses to surface a Program.cs startup throw isn't a stable contract
    /// to assert against; the message content is.</summary>
    private static bool ExceptionChainContains(Exception ex, string fragment)
    {
        if (ex.Message.Contains(fragment, StringComparison.Ordinal))
        {
            return true;
        }
        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Any(e => ExceptionChainContains(e, fragment));
        }
        return ex.InnerException is not null && ExceptionChainContains(ex.InnerException, fragment);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task DevSeedRoute_Returns404InProduction()
    {
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Production };
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task DevSeedRoute_IsReachableInDevelopment()
    {
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Development };
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}
