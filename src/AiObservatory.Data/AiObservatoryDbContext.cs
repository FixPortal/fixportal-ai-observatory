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
    public DbSet<CavemanSession> CavemanSessions => Set<CavemanSession>();
    public DbSet<ClaudeActivitySession> ClaudeActivitySessions => Set<ClaudeActivitySession>();
    public DbSet<GitHubPullRequest> GitHubPullRequests => Set<GitHubPullRequest>();
    public DbSet<GitHubCommit> GitHubCommits => Set<GitHubCommit>();
    public DbSet<GitHubWorkflowRun> GitHubWorkflowRuns => Set<GitHubWorkflowRun>();
    public DbSet<SpendCategory> SpendCategories => Set<SpendCategory>();
    public DbSet<SpendVendor> SpendVendors => Set<SpendVendor>();
    public DbSet<SpendEntry> SpendEntries => Set<SpendEntry>();

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
                // A subset can be neither negative nor larger than the set it is drawn from:
                // the five-minute remainder is derived by subtraction, so an over-large 1h
                // count would silently price part of the write twice.
                t.HasCheckConstraint(
                    "CK_UsageEvent_CacheWrite1hTokens_WithinCacheWrite",
                    "\"CacheWrite1hTokens\" IS NULL OR (\"CacheWrite1hTokens\" >= 0 AND \"CacheWrite1hTokens\" <= COALESCE(\"CacheWriteTokens\", 0))");
                t.HasCheckConstraint("CK_UsageEvent_CostUsd_NonNegative", "\"CostUsd\" >= 0");
            });
        });

        modelBuilder.Entity<DailyAggregate>(b =>
        {
            b.HasKey(d => new { d.Date, d.Provider, d.Model });
            b.Property(d => d.Provider).HasConversion<string>();
        });

        modelBuilder.Entity<SpendCategory>(b =>
        {
            b.Property(c => c.Key).HasMaxLength(60);
            b.Property(c => c.DisplayName).HasMaxLength(100);
            b.Property(c => c.ColorVar).HasMaxLength(60);
            b.HasIndex(c => c.Key).IsUnique();
        });

        modelBuilder.Entity<SpendVendor>(b =>
        {
            b.Property(v => v.Key).HasMaxLength(60);
            b.Property(v => v.DisplayName).HasMaxLength(100);
            b.Property(v => v.Provider).HasConversion<string>();
            b.HasIndex(v => v.Key).IsUnique();

            // Restrict: a category still referenced as a vendor's default must be archived,
            // not hard-deleted, so a hard delete fails loudly instead of silently clearing
            // the default.
            b.HasOne<SpendCategory>()
             .WithMany()
             .HasForeignKey(v => v.DefaultCategoryId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SpendEntry>(b =>
        {
            b.Property(e => e.Currency).HasMaxLength(3);
            b.Property(e => e.Description).HasMaxLength(200);
            b.Property(e => e.EntryKey).HasMaxLength(200);
            b.Property(e => e.Source).HasConversion<string>();
            b.HasIndex(e => e.OccurredOn);
            b.HasIndex(e => e.VendorId);
            b.HasIndex(e => e.CategoryId);

            // Idempotency, scoped per source. Filtered so manual rows (EntryKey null) are
            // exempt — PostgreSQL would allow repeated NULLs anyway, but the filter makes
            // the intent explicit and keeps the index small.
            b.HasIndex(e => new { e.Source, e.EntryKey })
             .IsUnique()
             .HasFilter("\"EntryKey\" IS NOT NULL");

            // Restrict: archiving is the soft delete for a vendor/category still in use, so
            // a hard delete of one with entries must fail loudly rather than cascade rows
            // out of the ledger.
            b.HasOne<SpendVendor>()
             .WithMany()
             .HasForeignKey(e => e.VendorId)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasOne<SpendCategory>()
             .WithMany()
             .HasForeignKey(e => e.CategoryId)
             .OnDelete(DeleteBehavior.Restrict);

            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_SpendEntry_Amount_NonNegative", "\"Amount\" >= 0");
                t.HasCheckConstraint("CK_SpendEntry_AmountGbp_NonNegative", "\"AmountGbp\" >= 0");
                t.HasCheckConstraint("CK_SpendEntry_FxRate_Positive", "\"FxRate\" > 0");
            });
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

        modelBuilder.Entity<CavemanSession>(b =>
        {
            b.Property(s => s.SessionId).HasMaxLength(200).IsRequired();
            b.HasIndex(s => s.SessionId).IsUnique();
            b.HasIndex(s => s.OccurredAt);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_CavemanSession_OutputTokens_NonNegative", "\"OutputTokens\" >= 0");
                t.HasCheckConstraint("CK_CavemanSession_EstSavedTokens_NonNegative", "\"EstSavedTokens\" >= 0");
                t.HasCheckConstraint("CK_CavemanSession_EstSavedUsd_NonNegative", "\"EstSavedUsd\" >= 0");
            });
        });

        modelBuilder.Entity<ClaudeActivitySession>(b =>
        {
            b.Property(s => s.SessionId).HasMaxLength(200).IsRequired();
            b.Property(s => s.Project).HasMaxLength(200).IsRequired();
            b.HasIndex(s => s.SessionId).IsUnique();
            b.HasIndex(s => s.StartedAt);
            b.HasIndex(s => s.Project);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ClaudeActivitySession_ActiveSeconds_NonNegative", "\"ActiveSeconds\" >= 0");
            });
        });

        modelBuilder.Entity<GitHubPullRequest>(b =>
        {
            b.Property(p => p.Repo).HasMaxLength(200).IsRequired();
            b.Property(p => p.Title).HasMaxLength(500).IsRequired();
            b.Property(p => p.Author).HasMaxLength(200).IsRequired();
            b.Property(p => p.State).HasMaxLength(20).IsRequired();
            b.HasIndex(p => new { p.Repo, p.Number }).IsUnique();
            b.HasIndex(p => p.CreatedAt);
            b.ToTable(t => t.HasCheckConstraint("CK_GitHubPullRequest_ReviewCount_NonNegative", "\"ReviewCount\" >= 0"));
        });

        modelBuilder.Entity<GitHubCommit>(b =>
        {
            b.Property(c => c.Repo).HasMaxLength(200).IsRequired();
            b.Property(c => c.Sha).HasMaxLength(64).IsRequired();
            b.Property(c => c.Author).HasMaxLength(200).IsRequired();
            b.HasIndex(c => new { c.Repo, c.Sha }).IsUnique();
            b.HasIndex(c => c.CommittedAt);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_GitHubCommit_Additions_NonNegative", "\"Additions\" >= 0");
                t.HasCheckConstraint("CK_GitHubCommit_Deletions_NonNegative", "\"Deletions\" >= 0");
            });
        });

        modelBuilder.Entity<GitHubWorkflowRun>(b =>
        {
            b.Property(r => r.Repo).HasMaxLength(200).IsRequired();
            b.Property(r => r.WorkflowName).HasMaxLength(200).IsRequired();
            b.Property(r => r.Status).HasMaxLength(20).IsRequired();
            b.HasIndex(r => new { r.Repo, r.RunId }).IsUnique();
            b.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<AdversarialReviewRun>(b =>
        {
            b.Property(r => r.Reviewer).HasMaxLength(100).IsRequired();
            b.Property(r => r.Model).HasMaxLength(200).IsRequired();
            b.Property(r => r.Role).HasMaxLength(20).IsRequired().HasDefaultValue("reviewer");
            b.Property(r => r.Repo).HasMaxLength(200);
            b.Property(r => r.Summary).HasMaxLength(80);
            b.Property(r => r.RunId).HasMaxLength(200).IsRequired();
            b.HasIndex(r => new { r.RunId, r.Reviewer, r.Role }).IsUnique();
            b.HasIndex(r => new { r.Reviewer, r.Model });
            b.HasIndex(r => r.RecordedAt);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_AdversarialReviewRun_InputTokens_NonNegative", "\"InputTokens\" >= 0");
                t.HasCheckConstraint("CK_AdversarialReviewRun_OutputTokens_NonNegative", "\"OutputTokens\" >= 0");
                t.HasCheckConstraint("CK_AdversarialReviewRun_CostUsd_NonNegative", "\"CostUsd\" >= 0");
                t.HasCheckConstraint("CK_AdversarialReviewRun_IssuesRaised_NonNegative", "\"IssuesRaised\" >= 0");
                t.HasCheckConstraint("CK_AdversarialReviewRun_IssuesAccepted_NonNegative", "\"IssuesAccepted\" >= 0");
            });
        });
    }
}
