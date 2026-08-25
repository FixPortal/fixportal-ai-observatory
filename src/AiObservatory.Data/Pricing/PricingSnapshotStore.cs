using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace AiObservatory.Data.Pricing;

public sealed class PricingSnapshotStore(AiObservatoryDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<PricingActivationResult> ActivateAsync(
        PricingSnapshotCandidate candidate,
        CancellationToken cancellationToken,
        Func<PricingSnapshot, CancellationToken, Task>? beforeCommit = null
    ) => await ActivateAsync(candidate, onlyIfMissing: false, cancellationToken, beforeCommit);

    public async Task<PricingActivationResult> ActivateIfMissingAsync(
        PricingSnapshotCandidate candidate,
        CancellationToken cancellationToken,
        Func<PricingSnapshot, CancellationToken, Task>? beforeCommit = null
    ) => await ActivateAsync(candidate, onlyIfMissing: true, cancellationToken, beforeCommit);

    private async Task<PricingActivationResult> ActivateAsync(
        PricingSnapshotCandidate candidate,
        bool onlyIfMissing,
        CancellationToken cancellationToken,
        Func<PricingSnapshot, CancellationToken, Task>? beforeCommit
    )
    {
        Validate(candidate);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({candidate.SourceId}, 0))",
                cancellationToken
            );

            if (
                onlyIfMissing
                && await db.PricingSnapshots.AnyAsync(
                    snapshot => snapshot.SourceId == candidate.SourceId && snapshot.IsActive,
                    cancellationToken
                )
            )
            {
                await transaction.CommitAsync(cancellationToken);
                return PricingActivationResult.Unchanged;
            }

            if (
                await db.PricingSnapshots.AnyAsync(
                    snapshot =>
                        snapshot.SourceId == candidate.SourceId && snapshot.ContentHash == candidate.ContentHash,
                    cancellationToken
                )
            )
            {
                await transaction.CommitAsync(cancellationToken);
                return PricingActivationResult.Unchanged;
            }

            await db
                .PricingSnapshots.Where(snapshot => snapshot.SourceId == candidate.SourceId && snapshot.IsActive)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(snapshot => snapshot.IsActive, false),
                    cancellationToken
                );

            var activated = new PricingSnapshot
            {
                Provider = candidate.Provider,
                SourceId = candidate.SourceId,
                RetrievedAt = candidate.RetrievedAt,
                SourceUrl = candidate.SourceUrl,
                ContentHash = candidate.ContentHash,
                RawEvidence = candidate.RawEvidence,
                NormalizedCatalog = candidate.NormalizedCatalog,
                IsActive = true,
            };
            db.PricingSnapshots.Add(activated);
            await db.SaveChangesAsync(cancellationToken);

            if (beforeCommit is not null)
            {
                await beforeCommit(activated, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return PricingActivationResult.Activated;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<PricingSnapshot?> GetActiveAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return db
            .PricingSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(snapshot => snapshot.SourceId == sourceId && snapshot.IsActive, cancellationToken);
    }

    public async Task<PricingSnapshot?> GetCatalogForDateAsync(
        Provider provider,
        LocalDate usageDate,
        CancellationToken cancellationToken = default
    )
    {
        var sourceId = provider switch
        {
            Provider.OpenAI => PricingSourceIds.OpenAi,
            Provider.Anthropic => PricingSourceIds.Claude,
            Provider.Moonshot => PricingSourceIds.Kimi,
            Provider.Google => PricingSourceIds.GoogleCloudCatalog,
            _ => null,
        };
        if (sourceId is null)
        {
            return null;
        }

        var snapshots = await db
            .PricingSnapshots.AsNoTracking()
            .Where(candidate => candidate.SourceId == sourceId)
            .OrderByDescending(candidate => candidate.RetrievedAt)
            .ThenByDescending(candidate => candidate.IsActive)
            .ToListAsync(cancellationToken);
        return snapshots.FirstOrDefault(snapshot => Covers(snapshot, usageDate));
    }

    private static bool Covers(PricingSnapshot snapshot, LocalDate usageDate) =>
        snapshot.Provider switch
        {
            Provider.OpenAI => Deserialize<OpenAiPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            Provider.Anthropic => Deserialize<AnthropicPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            Provider.Moonshot => Deserialize<KimiPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            Provider.Google => Deserialize<GooglePriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            _ => false,
        };

    private static void Validate(PricingSnapshotCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var expectedSourceId = candidate.Provider switch
        {
            Provider.OpenAI => PricingSourceIds.OpenAi,
            Provider.Anthropic => PricingSourceIds.Claude,
            Provider.Moonshot => PricingSourceIds.Kimi,
            Provider.Google => PricingSourceIds.GoogleCloudCatalog,
            _ => throw new ArgumentException("The provider has no first-party pricing source.", nameof(candidate)),
        };
        if (!string.Equals(candidate.SourceId, expectedSourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The pricing source does not match the provider.", nameof(candidate));
        }

        if (
            !Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || candidate.SourceUrl.Length > 2048
            || string.IsNullOrEmpty(candidate.ContentHash)
            || candidate.ContentHash.Length != 64
            || !candidate.ContentHash.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(candidate.RawEvidence)
            || string.IsNullOrWhiteSpace(candidate.NormalizedCatalog)
        )
        {
            throw new ArgumentException(
                "The pricing candidate contains invalid trust-boundary data.",
                nameof(candidate)
            );
        }

        var evidenceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.RawEvidence)));
        if (!string.Equals(candidate.ContentHash, evidenceHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The content hash must be the lowercase SHA-256 of the exact raw evidence.",
                nameof(candidate)
            );
        }

        try
        {
            var catalogSourceUrl = candidate.Provider switch
            {
                Provider.OpenAI => ValidateAndGetSource(Deserialize<OpenAiPriceCatalog>(candidate.NormalizedCatalog)),
                Provider.Anthropic => ValidateAndGetSource(
                    Deserialize<AnthropicPriceCatalog>(candidate.NormalizedCatalog)
                ),
                Provider.Moonshot => ValidateAndGetSource(Deserialize<KimiPriceCatalog>(candidate.NormalizedCatalog)),
                Provider.Google => ValidateAndGetSource(Deserialize<GooglePriceCatalog>(candidate.NormalizedCatalog)),
                _ => throw new UnreachableException(),
            };
            if (!string.Equals(catalogSourceUrl, candidate.SourceUrl, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The normalized catalog source URL does not match its evidence.");
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new ArgumentException("The normalized pricing catalog is invalid.", nameof(candidate), exception);
        }
    }

    private static string ValidateAndGetSource(OpenAiPriceCatalog catalog)
    {
        catalog.Validate();
        return catalog.SourceUrl;
    }

    private static string ValidateAndGetSource(AnthropicPriceCatalog catalog)
    {
        catalog.Validate();
        return catalog.SourceUrl;
    }

    private static string ValidateAndGetSource(KimiPriceCatalog catalog)
    {
        catalog.Validate();
        return catalog.SourceUrl;
    }

    private static string ValidateAndGetSource(GooglePriceCatalog catalog)
    {
        catalog.Validate();
        return catalog.SourceUrl;
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("The normalized pricing catalog is null.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        return options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    }
}
