using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Data;

public class AiObservatoryDbContext(DbContextOptions<AiObservatoryDbContext> options) : DbContext(options)
{
    // Seed ids for SpendCategory/SpendVendor (spec §2). Fixed rather than Guid.NewGuid()
    // so HasData produces the same INSERT every time a migration is scaffolded -- a
    // random id here would make every future `dotnet ef migrations add` see a phantom
    // diff and try to delete-and-recreate these rows.
    private static readonly Guid CodeReviewCategoryId = Guid.Parse("11111111-1111-1111-1111-111111111101");
    private static readonly Guid CreditsCategoryId = Guid.Parse("11111111-1111-1111-1111-111111111102");
    private static readonly Guid CiCategoryId = Guid.Parse("11111111-1111-1111-1111-111111111103");
    private static readonly Guid SubscriptionCategoryId = Guid.Parse("11111111-1111-1111-1111-111111111104");
    private static readonly Guid CloudCategoryId = Guid.Parse("11111111-1111-1111-1111-111111111105");

    private static readonly Guid AnthropicVendorId = Guid.Parse("22222222-2222-2222-2222-222222222201");
    private static readonly Guid GitHubActionsVendorId = Guid.Parse("22222222-2222-2222-2222-222222222202");
    private static readonly Guid CodeRabbitVendorId = Guid.Parse("22222222-2222-2222-2222-222222222203");
    private static readonly Guid GitarVendorId = Guid.Parse("22222222-2222-2222-2222-222222222204");
    private static readonly Guid MoonshotVendorId = Guid.Parse("22222222-2222-2222-2222-222222222205");
    private static readonly Guid OpenAiVendorId = Guid.Parse("22222222-2222-2222-2222-222222222206");
    private static readonly Guid GoogleVendorId = Guid.Parse("22222222-2222-2222-2222-222222222207");
    private static readonly Guid MicrosoftVendorId = Guid.Parse("22222222-2222-2222-2222-222222222208");
    private static readonly Guid OpenRouterVendorId = Guid.Parse("22222222-2222-2222-2222-222222222209");
    private static readonly Guid BlacksmithVendorId = Guid.Parse("22222222-2222-2222-2222-222222222210");
    private static readonly Guid CopilotVendorId = Guid.Parse("22222222-2222-2222-2222-222222222211");
    private static readonly Guid GitHubVendorId = Guid.Parse("22222222-2222-2222-2222-222222222212");

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
    public DbSet<IdeEvent> IdeEvents => Set<IdeEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UsageEvent>(b =>
        {
            b.Property(e => e.Provider).HasConversion<string>();
            b.Property(e => e.RawPayload).HasColumnType("jsonb");
            b.HasIndex(e => new { e.Provider, e.Model }).HasFilter("\"Model\" IS NOT NULL");
            b.Property(e => e.EventKey).HasMaxLength(200);
            b.Property(e => e.Runtime).HasMaxLength(100);
            b.Property(e => e.SessionId).HasMaxLength(200);
            b.Property(e => e.AgentId).HasMaxLength(200);
            // EventKey is a unique idempotency key scoped per provider.
            b.HasIndex(e => new { e.Provider, e.EventKey }).IsUnique().HasFilter("\"EventKey\" IS NOT NULL");

            b.HasIndex(e => e.OccurredAt);

            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_UsageEvent_InputTokens_NonNegative", "\"InputTokens\" >= 0");
                t.HasCheckConstraint("CK_UsageEvent_OutputTokens_NonNegative", "\"OutputTokens\" >= 0");
                t.HasCheckConstraint(
                    "CK_UsageEvent_CacheReadTokens_NonNegative",
                    "\"CacheReadTokens\" IS NULL OR \"CacheReadTokens\" >= 0"
                );
                t.HasCheckConstraint(
                    "CK_UsageEvent_CacheWriteTokens_NonNegative",
                    "\"CacheWriteTokens\" IS NULL OR \"CacheWriteTokens\" >= 0"
                );
                // A subset can be neither negative nor larger than the set it is drawn from:
                // the five-minute remainder is derived by subtraction, so an over-large 1h
                // count would silently price part of the write twice.
                t.HasCheckConstraint(
                    "CK_UsageEvent_CacheWrite1hTokens_WithinCacheWrite",
                    "\"CacheWrite1hTokens\" IS NULL OR (\"CacheWrite1hTokens\" >= 0 AND \"CacheWrite1hTokens\" <= COALESCE(\"CacheWriteTokens\", 0))"
                );
                t.HasCheckConstraint("CK_UsageEvent_ThoughtTokens_NonNegative", "\"ThoughtTokens\" IS NULL OR \"ThoughtTokens\" >= 0");
                t.HasCheckConstraint("CK_UsageEvent_CostUsd_NonNegative", "\"CostUsd\" IS NULL OR \"CostUsd\" >= 0");
            });
        });

        modelBuilder.Entity<DailyAggregate>(b =>
        {
            b.HasKey(d => new
            {
                d.Date,
                d.Provider,
                d.Model,
            });
            b.Property(d => d.Provider).HasConversion<string>();
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_DailyAggregate_InputTokens_NonNegative", "\"InputTokens\" >= 0");
                t.HasCheckConstraint("CK_DailyAggregate_OutputTokens_NonNegative", "\"OutputTokens\" >= 0");
                t.HasCheckConstraint("CK_DailyAggregate_CacheReadTokens_NonNegative", "\"CacheReadTokens\" >= 0");
                t.HasCheckConstraint("CK_DailyAggregate_CacheWriteTokens_NonNegative", "\"CacheWriteTokens\" >= 0");
                t.HasCheckConstraint(
                    "CK_DailyAggregate_CacheWrite1hTokens_WithinCacheWrite",
                    "\"CacheWrite1hTokens\" >= 0 AND \"CacheWrite1hTokens\" <= \"CacheWriteTokens\""
                );
                t.HasCheckConstraint("CK_DailyAggregate_CostUsd_NonNegative", "\"CostUsd\" >= 0");
                t.HasCheckConstraint("CK_DailyAggregate_UnknownCostCount_NonNegative", "\"UnknownCostCount\" >= 0");
                t.HasCheckConstraint("CK_DailyAggregate_RequestCount_NonNegative", "\"RequestCount\" >= 0");
            });
        });

        modelBuilder.Entity<SpendCategory>(b =>
        {
            b.Property(c => c.Key).HasMaxLength(60);
            b.Property(c => c.DisplayName).HasMaxLength(100);
            b.Property(c => c.ColorVar).HasMaxLength(60);
            b.HasIndex(c => c.Key).IsUnique();

            // Seeded so the entry form's pickers are non-empty on first deploy, rather than
            // an empty select and no way to add the vendors/categories that make it usable
            // (spec §2 names exactly the first four categories).
            //
            // Cloud is the fifth, added later: real spend showed £638.36 of Azure charges,
            // and cloud infrastructure is a genuinely different kind of spend from the other
            // four — it has no token estimate behind it, so folding it into Subscription
            // would inflate a category that is meant to be comparable against the estimate.
            b.HasData(
                new SpendCategory
                {
                    Id = CodeReviewCategoryId,
                    Key = "code-review",
                    DisplayName = "Code Review",
                    ColorVar = "--spend-code-review",
                    SortOrder = 10,
                },
                new SpendCategory
                {
                    Id = CreditsCategoryId,
                    Key = "credits",
                    DisplayName = "Credits",
                    ColorVar = "--spend-credits",
                    SortOrder = 20,
                },
                new SpendCategory
                {
                    Id = CiCategoryId,
                    Key = "ci",
                    DisplayName = "CI",
                    ColorVar = "--spend-ci",
                    SortOrder = 30,
                },
                new SpendCategory
                {
                    Id = SubscriptionCategoryId,
                    Key = "subscription",
                    DisplayName = "Subscription",
                    ColorVar = "--spend-subscription",
                    SortOrder = 40,
                },
                new SpendCategory
                {
                    Id = CloudCategoryId,
                    Key = "cloud",
                    DisplayName = "Cloud",
                    ColorVar = "--spend-cloud",
                    SortOrder = 50,
                }
            );
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

            // Provider is null for CodeRabbit, Gitar and GitHub Actions -- they have no
            // tokens to meter, which is the entire point of a separate vendor axis (spec §2).
            // Never assign one of these a Provider "helpfully"; that would fabricate a
            // token-estimate comparison that was never possible.
            b.HasData(
                new SpendVendor
                {
                    Id = AnthropicVendorId,
                    Key = "anthropic",
                    DisplayName = "Anthropic",
                    Provider = Provider.Anthropic,
                    DefaultCategoryId = CreditsCategoryId,
                },
                new SpendVendor
                {
                    Id = GitHubActionsVendorId,
                    Key = "github-actions",
                    DisplayName = "GitHub Actions",
                    Provider = null,
                    DefaultCategoryId = CiCategoryId,
                },
                new SpendVendor
                {
                    Id = CodeRabbitVendorId,
                    Key = "coderabbit",
                    DisplayName = "CodeRabbit",
                    Provider = null,
                    DefaultCategoryId = CodeReviewCategoryId,
                },
                new SpendVendor
                {
                    Id = GitarVendorId,
                    Key = "gitar",
                    DisplayName = "Gitar",
                    Provider = null,
                    DefaultCategoryId = CodeReviewCategoryId,
                },
                new SpendVendor
                {
                    Id = MoonshotVendorId,
                    Key = "moonshot",
                    DisplayName = "Moonshot",
                    Provider = Provider.Moonshot,
                    DefaultCategoryId = SubscriptionCategoryId,
                },
                // The five below carry real billed spend but had no vendor row, so none of it
                // could be recorded. OpenAI in particular was an obvious omission from the
                // original seed. Provider is set only where tokens are genuinely metered:
                // Microsoft/Azure, OpenRouter and Blacksmith have no Provider enum member and
                // no token estimate, so they stay null for the same reason CodeRabbit does.
                new SpendVendor
                {
                    Id = OpenAiVendorId,
                    Key = "openai",
                    DisplayName = "OpenAI",
                    Provider = Provider.OpenAI,
                    DefaultCategoryId = SubscriptionCategoryId,
                },
                new SpendVendor
                {
                    Id = GoogleVendorId,
                    Key = "google",
                    DisplayName = "Google",
                    Provider = Provider.Google,
                    DefaultCategoryId = SubscriptionCategoryId,
                },
                new SpendVendor
                {
                    Id = MicrosoftVendorId,
                    Key = "microsoft",
                    DisplayName = "Microsoft",
                    Provider = null,
                    DefaultCategoryId = CloudCategoryId,
                },
                new SpendVendor
                {
                    Id = OpenRouterVendorId,
                    Key = "openrouter",
                    DisplayName = "OpenRouter",
                    Provider = null,
                    DefaultCategoryId = SubscriptionCategoryId,
                },
                new SpendVendor
                {
                    Id = BlacksmithVendorId,
                    Key = "blacksmith",
                    DisplayName = "Blacksmith",
                    Provider = null,
                    DefaultCategoryId = CiCategoryId,
                },
                // Distinct from GitHub Actions on purpose. The ingest worker has been
                // recording Copilot token usage since 2026-05-26, but there was no vendor to
                // book the matching charge against, so Copilot was the one metered provider
                // that could never be compared against its own estimate.
                new SpendVendor
                {
                    Id = CopilotVendorId,
                    Key = "copilot",
                    DisplayName = "GitHub Copilot",
                    Provider = Provider.Copilot,
                    DefaultCategoryId = SubscriptionCategoryId,
                },
                // Everything GitHub bills that is NOT Actions compute: Advanced Security,
                // Code Quality AI Credits, and any product line added later. Kept separate
                // from github-actions because that vendor's display name is a promise about
                // what the charge was for, and booking a Code Quality credit against
                // "GitHub Actions" would misattribute it in every breakdown. Provider stays
                // null: GitHub bills these in dollars with no token count behind them.
                new SpendVendor
                {
                    Id = GitHubVendorId,
                    Key = "github",
                    DisplayName = "GitHub",
                    Provider = null,
                    DefaultCategoryId = SubscriptionCategoryId,
                }
            );
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
            b.HasIndex(e => new { e.Source, e.EntryKey }).IsUnique().HasFilter("\"EntryKey\" IS NOT NULL");

            // Restrict: archiving is the soft delete for a vendor/category still in use, so
            // a hard delete of one with entries must fail loudly rather than cascade rows
            // out of the ledger.
            b.HasOne<SpendVendor>().WithMany().HasForeignKey(e => e.VendorId).OnDelete(DeleteBehavior.Restrict);

            b.HasOne<SpendCategory>().WithMany().HasForeignKey(e => e.CategoryId).OnDelete(DeleteBehavior.Restrict);

            b.ToTable(t =>
            {
                // Signed, not non-negative: a refund is a negative amount, which keeps
                // AmountGbp the one column every aggregate sums unconditionally. See
                // SpendEntry.Amount for why a refund flag was rejected. Zero stays barred —
                // it is a data-entry mistake in either direction, and barring it keeps some
                // of the protection the non-negative constraint used to give.
                t.HasCheckConstraint("CK_SpendEntry_Amount_NonZero", "\"Amount\" <> 0");

                // AmountGbp is the column every total sums, so it needs the invariant more
                // than Amount does: a zero or opposite-sign value there turns a refund into
                // a charge (or erases one) in every aggregate at once, invisibly. The
                // product form asserts non-zero and same-sign together.
                //
                // It also rejects a charge so small that the conversion rounds to nothing at
                // 4dp — deliberate. Such a row cannot contribute to a total anyway, and
                // SaveRowAsync's DbUpdateException catch turns it into a per-row "rejected"
                // verdict rather than failing the batch.
                t.HasCheckConstraint("CK_SpendEntry_AmountGbp_SameSign", "\"Amount\" * \"AmountGbp\" > 0");
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
            b.ToTable(t =>
                t.HasCheckConstraint("CK_GitHubPullRequest_ReviewCount_NonNegative", "\"ReviewCount\" >= 0")
            );
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
            b.HasIndex(
                    nameof(AdversarialReviewRun.RunId),
                    nameof(AdversarialReviewRun.Reviewer),
                    nameof(AdversarialReviewRun.Role)
                )
                .IsUnique();
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

        modelBuilder.Entity<IdeEvent>(b =>
        {
            b.Property(e => e.IdempotencyKey).HasMaxLength(256).IsRequired();
            b.Property(e => e.EventType).HasMaxLength(128).IsRequired();
            b.Property(e => e.EnvelopeJson).HasColumnType("jsonb").IsRequired();
            b.Property(e => e.ContentSha256).HasMaxLength(71).IsRequired();
            b.HasIndex(e => new { e.PartnerId, e.IdempotencyKey }).IsUnique();
            b.HasIndex(e => e.OccurredAt);
        });
    }
}
