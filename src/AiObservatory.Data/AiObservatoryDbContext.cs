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
            b.Property(c => c.Sha).HasMaxLength(40).IsRequired();
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
