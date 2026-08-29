using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Pricing;

public sealed class PricingSnapshotStore(AiObservatoryDbContext db)
{
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

    public Task<PricingSnapshot?> GetCatalogForDateAsync(
        Provider provider,
        LocalDate usageDate,
        CancellationToken cancellationToken = default
    ) => GetCatalogForDateAsync(GetSourceId(provider), usageDate, snapshotsBySourceId: null, cancellationToken);

    public Task<PricingSnapshot?> GetCatalogForDateAsync(
        UsageEvent usage,
        CancellationToken cancellationToken = default
    ) => GetCatalogForDateAsync(usage, snapshotsBySourceId: null, cancellationToken);

    /// <summary>
    /// As <see cref="GetCatalogForDateAsync(UsageEvent, CancellationToken)"/>, but reads each source's
    /// snapshot rows through <paramref name="snapshotsBySourceId"/>, so a pass over many events queries
    /// them once rather than once per event. The effective-date filter still runs per event, so events on
    /// different dates resolve to different snapshots exactly as they do uncached.
    /// </summary>
    internal Task<PricingSnapshot?> GetCatalogForDateAsync(
        UsageEvent usage,
        Dictionary<string, List<PricingSnapshot>>? snapshotsBySourceId,
        CancellationToken cancellationToken
    ) =>
        usage.CostBasis == CostBasis.Notional
            ? GetActiveForUsageAsync(usage, cancellationToken)
            : GetCatalogForDateAsync(
                GetSourceId(usage),
                usage.OccurredAt.InUtc().Date,
                snapshotsBySourceId,
                cancellationToken
            );

    private Task<PricingSnapshot?> GetActiveForUsageAsync(UsageEvent usage, CancellationToken cancellationToken)
    {
        var sourceId = GetSourceId(usage);
        return sourceId is null ? Task.FromResult<PricingSnapshot?>(null) : GetActiveAsync(sourceId, cancellationToken);
    }

    private async Task<PricingSnapshot?> GetCatalogForDateAsync(
        string? sourceId,
        LocalDate usageDate,
        Dictionary<string, List<PricingSnapshot>>? snapshotsBySourceId,
        CancellationToken cancellationToken
    )
    {
        if (sourceId is null)
        {
            return null;
        }

        if (snapshotsBySourceId is null || !snapshotsBySourceId.TryGetValue(sourceId, out var snapshots))
        {
            snapshots = await db
                .PricingSnapshots.AsNoTracking()
                .Where(candidate => candidate.SourceId == sourceId)
                .OrderByDescending(candidate => candidate.RetrievedAt)
                .ThenByDescending(candidate => candidate.IsActive)
                .ToListAsync(cancellationToken);
            snapshotsBySourceId?.Add(sourceId, snapshots);
        }

        return snapshots.FirstOrDefault(snapshot => Covers(snapshot, usageDate));
    }

    internal Task AcquireSharedActivationLockAsync(Provider provider, CancellationToken cancellationToken = default) =>
        AcquireSharedActivationLockAsync(GetSourceId(provider), cancellationToken);

    internal Task AcquireSharedActivationLockAsync(UsageEvent usage, CancellationToken cancellationToken = default) =>
        AcquireSharedActivationLockAsync(GetSourceId(usage), cancellationToken);

    private async Task AcquireSharedActivationLockAsync(string? sourceId, CancellationToken cancellationToken)
    {
        if (sourceId is null)
        {
            return;
        }

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A pricing read lock requires an active database transaction.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock_shared(hashtextextended({sourceId}, 0))",
            cancellationToken
        );
    }

    private static bool Covers(PricingSnapshot snapshot, LocalDate usageDate) =>
        snapshot.Provider switch
        {
            Provider.OpenAI => PricingCatalogJson
                .Deserialize<OpenAiPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            Provider.Anthropic => PricingCatalogJson
                .Deserialize<AnthropicPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            Provider.Moonshot => PricingCatalogJson
                .Deserialize<KimiPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            Provider.Google when snapshot.SourceId == PricingSourceIds.GeminiDeveloperApi => PricingCatalogJson
                .Deserialize<GeminiDeveloperPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            Provider.Google => PricingCatalogJson
                .Deserialize<GooglePriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Any(entry => entry.EffectiveFrom <= usageDate),
            _ => false,
        };

    private static string? GetSourceId(Provider provider) =>
        provider switch
        {
            Provider.OpenAI => PricingSourceIds.OpenAi,
            Provider.Anthropic => PricingSourceIds.Claude,
            Provider.Moonshot => PricingSourceIds.Kimi,
            Provider.Google => PricingSourceIds.GoogleCloudCatalog,
            _ => null,
        };

    private static string? GetSourceId(UsageEvent usage)
    {
        if (usage.Provider == Provider.Google && usage.CostBasis == CostBasis.Notional)
        {
            return PricingSourceIds.GeminiDeveloperApi;
        }

        if (usage.Provider != Provider.Google)
        {
            return GetSourceId(usage.Provider);
        }

        using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
        return
            ProviderPricingJson.TryString(evidence.RootElement, "service", out var service)
            && service.Equals("Gemini Developer API", StringComparison.OrdinalIgnoreCase)
            ? PricingSourceIds.GeminiDeveloperApi
            : PricingSourceIds.GoogleCloudCatalog;
    }

    private static void Validate(PricingSnapshotCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var expectedSourceId = GetSourceId(candidate.Provider);
        if (expectedSourceId is null)
        {
            throw new ArgumentException("The provider has no first-party pricing source.", nameof(candidate));
        }
        if (
            !string.Equals(candidate.SourceId, expectedSourceId, StringComparison.Ordinal)
            && !(candidate.Provider == Provider.Google && candidate.SourceId == PricingSourceIds.GeminiDeveloperApi)
        )
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
                Provider.OpenAI => ValidateAndGetSource(
                    PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(candidate.NormalizedCatalog)
                ),
                Provider.Anthropic => ValidateAndGetSource(
                    PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(candidate.NormalizedCatalog)
                ),
                Provider.Moonshot => ValidateAndGetSource(
                    PricingCatalogJson.Deserialize<KimiPriceCatalog>(candidate.NormalizedCatalog)
                ),
                Provider.Google when candidate.SourceId == PricingSourceIds.GeminiDeveloperApi => ValidateAndGetSource(
                    PricingCatalogJson.Deserialize<GeminiDeveloperPriceCatalog>(candidate.NormalizedCatalog)
                ),
                Provider.Google => ValidateAndGetSource(
                    PricingCatalogJson.Deserialize<GooglePriceCatalog>(candidate.NormalizedCatalog)
                ),
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

    private static string ValidateAndGetSource(GeminiDeveloperPriceCatalog catalog)
    {
        catalog.Validate();
        return catalog.SourceUrl;
    }
}
