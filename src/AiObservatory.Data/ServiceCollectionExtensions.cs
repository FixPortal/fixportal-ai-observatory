using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Data;

public static class ServiceCollectionExtensions
{
    // Returning the service collection is the standard fluent DI-registration convention.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IServiceCollection AddDataLayer(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<AiObservatoryDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.UseNodaTime()));
        services.AddScoped<IUsageRepository, UsageRepository>();
        services.AddScoped<IAdversarialReviewRepository, AdversarialReviewRepository>();
        services.AddScoped<IGitHubActivityRepository, GitHubActivityRepository>();
        return services;
    }
}
