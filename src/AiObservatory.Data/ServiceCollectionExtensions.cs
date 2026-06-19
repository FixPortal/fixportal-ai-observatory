using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Data;

public static class ServiceCollectionExtensions
{
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
        return services;
    }
}
