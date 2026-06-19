using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Data;

public class AiObservatoryDbContext(DbContextOptions<AiObservatoryDbContext> options)
    : DbContext(options)
{
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<DailyAggregate> DailyAggregates => Set<DailyAggregate>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Insight> Insights => Set<Insight>();
    public DbSet<BudgetRule> BudgetRules => Set<BudgetRule>();
    public DbSet<AdversarialReviewRun> AdversarialReviewRuns => Set<AdversarialReviewRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UsageEvent>(b =>
        {
            b.Property(e => e.Provider).HasConversion<string>();
            b.Property(e => e.RawPayload).HasColumnType("jsonb");
            b.HasIndex(e => new { e.Provider, e.Model })
             .HasFilter("\"Model\" IS NOT NULL");
            b.Property(e => e.EventKey).HasMaxLength(200);
            // EventKey is a unique idempotency key scoped per provider.
            b.HasIndex(e => new { e.Provider, e.EventKey })
             .IsUnique()
             .HasFilter("\"EventKey\" IS NOT NULL");

            b.HasIndex(e => e.OccurredAt);

            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_UsageEvent_InputTokens_NonNegative", "\"InputTokens\" >= 0");
                t.HasCheckConstraint("CK_UsageEvent_OutputTokens_NonNegative", "\"OutputTokens\" >= 0");
                t.HasCheckConstraint("CK_UsageEvent_CacheReadTokens_NonNegative", "\"CacheReadTokens\" IS NULL OR \"CacheReadTokens\" >= 0");
                t.HasCheckConstraint("CK_UsageEvent_CacheWriteTokens_NonNegative", "\"CacheWriteTokens\" IS NULL OR \"CacheWriteTokens\" >= 0");
                t.HasCheckConstraint("CK_UsageEvent_CostUsd_NonNegative", "\"CostUsd\" >= 0");
            });
        });

        modelBuilder.Entity<DailyAggregate>(b =>
        {
            b.HasKey(d => new { d.Date, d.Provider, d.Model });
            b.Property(d => d.Provider).HasConversion<string>();
        });

        modelBuilder.Entity<Subscription>(b =>
        {
            b.Property(s => s.Provider).HasConversion<string>();
        });

        modelBuilder.Entity<Insight>(b =>
        {
            b.Property(i => i.InsightType).HasConversion<string>();
            b.Property(i => i.Data).HasColumnType("jsonb");
        });

        modelBuilder.Entity<BudgetRule>(b =>
        {
            b.Property(r => r.Provider).HasConversion<string>();
            b.Property(r => r.Period).HasConversion<string>();
        });

        modelBuilder.Entity<AdversarialReviewRun>(b =>
        {
            b.Property(r => r.Reviewer).HasMaxLength(100).IsRequired();
            b.Property(r => r.Model).HasMaxLength(200).IsRequired();
            b.Property(r => r.RunId).HasMaxLength(200).IsRequired();
            b.HasIndex(r => r.RunId).IsUnique();
            b.HasIndex(r => new { r.Reviewer, r.Model });
            b.HasIndex(r => r.RecordedAt);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_AdversarialReviewRun_InputTokens_NonNegative", "\"InputTokens\" >= 0");
                t.HasCheckConstraint("CK_AdversarialReviewRun_OutputTokens_NonNegative", "\"OutputTokens\" >= 0");
                t.HasCheckConstraint("CK_AdversarialReviewRun_CostUsd_NonNegative", "\"CostUsd\" >= 0");
                t.HasCheckConstraint("CK_AdversarialReviewRun_IssuesRaised_NonNegative", "\"IssuesRaised\" >= 0");
                t.HasCheckConstraint("CK_AdversarialReviewRun_IssuesAccepted_Valid", "\"IssuesAccepted\" >= 0 AND \"IssuesAccepted\" <= \"IssuesRaised\"");
            });
        });
    }
}
