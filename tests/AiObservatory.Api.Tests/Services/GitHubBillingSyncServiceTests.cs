using AiObservatory.Api.Services.Fx;
using AiObservatory.Api.Services.GitHub;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiObservatory.Api.Tests.Services;

/// <summary>
/// The sync folds GitHub's own bill into the ledger. Every assertion here is about money
/// landing correctly: the right vendor and category, the amount converted once at the month's
/// rate, an open month converging as it accrues, and a single bad line never costing the rest
/// of the bill.
/// <para>
/// Unit-lane, so the EF in-memory provider stands in for PostgreSQL. It does not enforce
/// check constraints or unique indexes — the constraint behaviour itself is the integration
/// project's job. What is covered here is the service's own decisions.
/// </para>
/// </summary>
public class GitHubBillingSyncServiceTests
{
    private static readonly Guid GithubActionsVendorId = Guid.NewGuid();
    private static readonly Guid GithubVendorId = Guid.NewGuid();
    private static readonly Guid CiCategoryId = Guid.NewGuid();
    private static readonly Guid CodeReviewCategoryId = Guid.NewGuid();
    private static readonly Guid SubscriptionCategoryId = Guid.NewGuid();

    private static readonly Instant Now = Instant.FromUtc(2026, 7, 30, 9, 0);

    /// <summary>A context on its own in-memory store, seeded with the catalog the sync expects.</summary>
    private static AiObservatoryDbContext NewDb(
        bool seedCatalog = true,
        params IInterceptor[] interceptors
    )
    {
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptors)
            .Options;
        var db = new AiObservatoryDbContext(options);

        if (seedCatalog)
        {
            db.SpendVendors.AddRange(
                new SpendVendor
                {
                    Id = GithubActionsVendorId,
                    Key = "github-actions",
                    DisplayName = "GitHub Actions",
                },
                new SpendVendor
                {
                    Id = GithubVendorId,
                    Key = "github",
                    DisplayName = "GitHub",
                }
            );
            db.SpendCategories.AddRange(
                new SpendCategory
                {
                    Id = CiCategoryId,
                    Key = "ci",
                    DisplayName = "CI",
                    ColorVar = "",
                },
                new SpendCategory
                {
                    Id = CodeReviewCategoryId,
                    Key = "code-review",
                    DisplayName = "Code Review",
                    ColorVar = "",
                },
                new SpendCategory
                {
                    Id = SubscriptionCategoryId,
                    Key = "subscription",
                    DisplayName = "Subscription",
                    ColorVar = "",
                }
            );
            db.SaveChanges();
        }

        return db;
    }

    /// <summary>
    /// A billing client returning <paramref name="items"/> for the current year and nothing for
    /// the year before, which is the shape a mid-year run actually sees.
    /// </summary>
    private static GitHubBillingClient ClientReturning(params GitHubBillingUsageItem[] items)
    {
        var client = Substitute.For<GitHubBillingClient>(
            new HttpClient(),
            "FixPortal",
            NullLogger<GitHubBillingClient>.Instance
        );
        client.GetUsageAsync(2025, Arg.Any<CancellationToken>()).Returns([]);
        client.GetUsageAsync(2026, Arg.Any<CancellationToken>()).Returns(items);
        return client;
    }

    /// <summary>An FX provider frozen at <paramref name="rate"/> for every date.</summary>
    private static FxRateProvider FxAt(decimal rate)
    {
        var fx = Substitute.For<FxRateProvider>(
            new HttpClient(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<FxRateProvider>.Instance
        );
        fx.GetGbpRateOnAsync("USD", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(rate);
        return fx;
    }

    private static GitHubBillingSyncService Create(
        AiObservatoryDbContext db,
        GitHubBillingClient client,
        FxRateProvider fx
    ) => new(client, db, fx, new FakeClock(Now), NullLogger<GitHubBillingSyncService>.Instance);

    private static GitHubBillingUsageItem Item(
        string product,
        string sku,
        decimal netAmount,
        int month = 7,
        int day = 1
    ) => new(new DateOnly(2026, month, day), product, sku, netAmount);

    [Fact]
    public async Task WritesAnEntryPerBillingLine()
    {
        await using var db = NewDb();
        var sut = Create(db, ClientReturning(Item("actions", "Actions Linux", 100m)), FxAt(0.75m));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry.Amount.Should().Be(100m);
        entry.Currency.Should().Be("USD", "every GitHub charge is billed in USD");
        entry.AmountGbp.Should().Be(75m);
        entry.FxRate.Should().Be(0.75m);
        entry.Source.Should().Be(SpendSource.Api);
        entry.RecordedAt.Should().Be(Now);
        entry
            .OccurredOn.Should()
            .Be(
                new LocalDate(2026, 7, 1),
                "usage is monthly, so the entry is dated to the start of its month"
            );
    }

    /// <summary>
    /// The product map is the whole attribution decision. Booking Advanced Security or a Code
    /// Quality AI Credit against a vendor named "GitHub Actions" would misattribute it in every
    /// breakdown on the dashboard, and an unknown product must still land rather than vanish.
    /// </summary>
    [Theory]
    [InlineData("actions", "github-actions", "ci")]
    [InlineData("packages", "github-actions", "ci")]
    [InlineData("code_quality", "github", "code-review")]
    [InlineData("ghas", "github", "subscription")]
    [InlineData("some_new_product", "github", "subscription")]
    public async Task BooksEachProductAgainstItsVendorAndCategory(
        string product,
        string expectedVendorKey,
        string expectedCategoryKey
    )
    {
        await using var db = NewDb();
        var sut = Create(db, ClientReturning(Item(product, "a sku", 10m)), FxAt(0.8m));

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        var vendor = await db.SpendVendors.SingleAsync(
            v => v.Id == entry.VendorId,
            TestContext.Current.CancellationToken
        );
        var category = await db.SpendCategories.SingleAsync(
            c => c.Id == entry.CategoryId,
            TestContext.Current.CancellationToken
        );
        vendor.Key.Should().Be(expectedVendorKey);
        category.Key.Should().Be(expectedCategoryKey);
    }

    [Fact]
    public async Task MatchesTheProductNameCaseInsensitively()
    {
        await using var db = NewDb();
        var sut = Create(db, ClientReturning(Item("ACTIONS", "Actions Linux", 10m)), FxAt(0.8m));

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry
            .VendorId.Should()
            .Be(
                GithubActionsVendorId,
                "a casing change on GitHub's side must not silently reroute the charge to the fallback vendor"
            );
    }

    /// <summary>
    /// GitHub reports usage per repository, so the same SKU can arrive several times in one
    /// month. Summing keeps one ledger row per (month, product, SKU) — without it the second
    /// row would collide on the entry key and the month would be understated.
    /// </summary>
    [Fact]
    public async Task SumsRepeatedLinesForTheSameMonthProductAndSku()
    {
        await using var db = NewDb();
        var sut = Create(
            db,
            ClientReturning(
                Item("actions", "Actions Linux", 10m, day: 1),
                Item("actions", "Actions Linux", 15m, day: 1)
            ),
            FxAt(1m)
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry.Amount.Should().Be(25m);
    }

    [Fact]
    public async Task KeepsDifferentSkusInTheSameMonthApart()
    {
        await using var db = NewDb();
        var sut = Create(
            db,
            ClientReturning(
                Item("actions", "Actions Linux", 10m),
                Item("actions", "Actions Windows", 15m)
            ),
            FxAt(1m)
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(2);
        var amounts = await db
            .SpendEntries.Select(e => e.Amount)
            .ToListAsync(TestContext.Current.CancellationToken);
        amounts.Should().BeEquivalentTo([10m, 15m]);
    }

    [Fact]
    public async Task DropsAFullyDiscountedLine()
    {
        await using var db = NewDb();
        var sut = Create(db, ClientReturning(Item("actions", "Included minutes", 0m)), FxAt(0.8m));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(0);
        db.SpendEntries.Should()
            .BeEmpty(
                "allowance usage that was never billed is not spend, and CK_SpendEntry_Amount_NonZero would reject it anyway"
            );
    }

    [Fact]
    public async Task KeepsARefundLine()
    {
        await using var db = NewDb();
        var sut = Create(db, ClientReturning(Item("actions", "Credit", -20m)), FxAt(0.5m));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry.Amount.Should().Be(-20m);
        entry
            .AmountGbp.Should()
            .Be(-10m, "AmountGbp carries the sign so a plain SUM nets credits off charges");
    }

    [Fact]
    public async Task ReturnsZeroWhenGitHubReportsNoUsage()
    {
        await using var db = NewDb();
        var sut = Create(db, ClientReturning(), FxAt(0.8m));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(0);
        db.SpendEntries.Should().BeEmpty();
    }

    /// <summary>
    /// A January run must still pick up the December that closed days earlier, so the sync asks
    /// for the previous year as well as the current one.
    /// </summary>
    [Fact]
    public async Task FetchesThePreviousYearAsWellAsTheCurrentOne()
    {
        await using var db = NewDb();
        var client = ClientReturning(Item("actions", "Actions Linux", 10m));
        var sut = Create(db, client, FxAt(0.8m));

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        await client.Received(1).GetUsageAsync(2025, Arg.Any<CancellationToken>());
        await client.Received(1).GetUsageAsync(2026, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConvertsAtTheRateForTheChargeMonthNotToday()
    {
        await using var db = NewDb();
        var fx = Substitute.For<FxRateProvider>(
            new HttpClient(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<FxRateProvider>.Instance
        );
        fx.GetGbpRateOnAsync("USD", new LocalDate(2026, 5, 1), Arg.Any<CancellationToken>())
            .Returns(0.70m);
        fx.GetGbpRateOnAsync("USD", new LocalDate(2026, 7, 1), Arg.Any<CancellationToken>())
            .Returns(0.80m);
        var sut = Create(
            db,
            ClientReturning(
                Item("actions", "Actions Linux", 100m, month: 5),
                Item("actions", "Actions Linux", 100m, month: 7)
            ),
            fx
        );

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var may = await db.SpendEntries.SingleAsync(
            e => e.OccurredOn == new LocalDate(2026, 5, 1),
            TestContext.Current.CancellationToken
        );
        var july = await db.SpendEntries.SingleAsync(
            e => e.OccurredOn == new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );
        may.AmountGbp.Should().Be(70m);
        july.AmountGbp.Should().Be(80m);
    }

    [Fact]
    public async Task RoundsTheConversionToFourDecimalPlaces()
    {
        await using var db = NewDb();
        var sut = Create(
            db,
            ClientReturning(Item("code_quality", "Code Quality AI Credits", 12.0141527m)),
            FxAt(0.7412m)
        );

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        // 12.0141527 * 0.7412 = 8.90489... -> 8.9049 at 4dp.
        entry.AmountGbp.Should().Be(8.9049m);
    }

    /// <summary>
    /// A line worth less than half a hundredth of a penny leaves Amount non-zero while
    /// AmountGbp rounds to zero, which violates CK_SpendEntry_AmountGbp_SameSign. Skipping it
    /// keeps that a logged skip rather than a DbUpdateException, and it cannot move a total.
    /// </summary>
    [Fact]
    public async Task SkipsALineThatRoundsToZeroGbp()
    {
        await using var db = NewDb();
        var sut = Create(
            db,
            ClientReturning(Item("actions", "A rounding crumb", 0.00001m)),
            FxAt(0.8m)
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(0);
        db.SpendEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipsTheLineRatherThanFailWhenTheCatalogRowIsMissing()
    {
        await using var db = NewDb(seedCatalog: false);
        var sut = Create(db, ClientReturning(Item("actions", "Actions Linux", 10m)), FxAt(0.8m));

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(0);
        db.SpendEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task StillWritesTheOtherLinesWhenOneHasNoCatalogRow()
    {
        await using var db = NewDb();
        // Remove the vendor 'actions' books against; 'code_quality' books against 'github'
        // and must still land.
        db.SpendVendors.Remove(
            await db.SpendVendors.SingleAsync(
                v => v.Id == GithubActionsVendorId,
                TestContext.Current.CancellationToken
            )
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var sut = Create(
            db,
            ClientReturning(
                Item("actions", "Actions Linux", 10m),
                Item("code_quality", "Code Quality AI Credits", 20m)
            ),
            FxAt(1m)
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry.Amount.Should().Be(20m, "a missing catalog row must cost only its own line");
    }

    [Fact]
    public async Task SkipsALineWhoseRateCannotBeResolved()
    {
        await using var db = NewDb();
        var fx = Substitute.For<FxRateProvider>(
            new HttpClient(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<FxRateProvider>.Instance
        );
        fx.GetGbpRateOnAsync("USD", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new FxUnavailableException("USD", new LocalDate(2026, 7, 1)));
        var sut = Create(db, ClientReturning(Item("actions", "Actions Linux", 10m)), fx);

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(0);
        db.SpendEntries.Should()
            .BeEmpty("skipping beats freezing a wrong rate onto a permanent ledger row");
    }

    /// <summary>
    /// The current month's usage grows every day, so a second run must move the existing row
    /// rather than insert a second one — that convergence is the reason this upserts at all.
    /// </summary>
    [Fact]
    public async Task UpdatesTheExistingRowWhenAnOpenMonthHasGrown()
    {
        await using var db = NewDb();
        await Create(db, ClientReturning(Item("actions", "Actions Linux", 100m)), FxAt(0.75m))
            .SyncAsync(TestContext.Current.CancellationToken);

        var written = await Create(
                db,
                ClientReturning(Item("actions", "Actions Linux", 133.60m)),
                FxAt(0.80m)
            )
            .SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry.Amount.Should().Be(133.60m);
        entry.AmountGbp.Should().Be(106.88m);
        entry.FxRate.Should().Be(0.80m);
    }

    [Fact]
    public async Task ReportsNothingWrittenWhenAClosedMonthIsUnchanged()
    {
        await using var db = NewDb();
        await Create(db, ClientReturning(Item("actions", "Actions Linux", 100m)), FxAt(0.75m))
            .SyncAsync(TestContext.Current.CancellationToken);

        var written = await Create(
                db,
                ClientReturning(Item("actions", "Actions Linux", 100m)),
                FxAt(0.75m)
            )
            .SyncAsync(TestContext.Current.CancellationToken);

        written
            .Should()
            .Be(0, "re-running must be free, so an unchanged line is not reported as written");
        db.SpendEntries.Should().HaveCount(1);
    }

    /// <summary>
    /// Only the amount moves on an update. A recategorisation done by hand on the dashboard
    /// must survive the next sync, or the sync would silently undo the user's correction.
    /// </summary>
    [Fact]
    public async Task LeavesAManualRecategorisationAloneWhenTheAmountChanges()
    {
        await using var db = NewDb();
        await Create(db, ClientReturning(Item("actions", "Actions Linux", 100m)), FxAt(0.75m))
            .SyncAsync(TestContext.Current.CancellationToken);
        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry.VendorId = GithubVendorId;
        entry.CategoryId = SubscriptionCategoryId;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Create(db, ClientReturning(Item("actions", "Actions Linux", 150m)), FxAt(0.75m))
            .SyncAsync(TestContext.Current.CancellationToken);

        var updated = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        updated.Amount.Should().Be(150m);
        updated.VendorId.Should().Be(GithubVendorId);
        updated.CategoryId.Should().Be(SubscriptionCategoryId);
    }

    /// <summary>
    /// A manual row for the same month must not be adopted by the sync: it only ever matches
    /// its own <c>SpendSource.Api</c> rows, so a hand-typed charge is never overwritten.
    /// </summary>
    [Fact]
    public async Task IgnoresAManualEntryAndInsertsItsOwn()
    {
        await using var db = NewDb();
        db.SpendEntries.Add(
            new SpendEntry
            {
                OccurredOn = new LocalDate(2026, 7, 1),
                VendorId = GithubActionsVendorId,
                CategoryId = CiCategoryId,
                Amount = 999m,
                Currency = "USD",
                AmountGbp = 799m,
                FxRate = 0.8m,
                Source = SpendSource.Manual,
                EntryKey = "github:2026-07:actions:Actions Linux",
                RecordedAt = Now,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var sut = Create(db, ClientReturning(Item("actions", "Actions Linux", 10m)), FxAt(0.8m));

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var manual = await db.SpendEntries.SingleAsync(
            e => e.Source == SpendSource.Manual,
            TestContext.Current.CancellationToken
        );
        manual.Amount.Should().Be(999m, "a hand-entered charge is never the sync's to rewrite");
        db.SpendEntries.Should().HaveCount(2);
    }

    /// <summary>
    /// A row the database rejects must cost only itself. Saving per line is what makes that
    /// true, and the failed entity has to be detached or the very next line's save retries it
    /// and fails again — one bad row would then take the whole cycle's GitHub spend with it.
    /// </summary>
    [Fact]
    public async Task OneRejectedRowDoesNotTakeTheRestOfTheBillWithIt()
    {
        var interceptor = new RejectSkuInterceptor("Actions Linux");
        await using var db = NewDb(seedCatalog: true, interceptor);
        var sut = Create(
            db,
            ClientReturning(
                Item("actions", "Actions Linux", 10m),
                Item("actions", "Actions Windows", 20m),
                Item("code_quality", "Code Quality AI Credits", 30m)
            ),
            FxAt(1m)
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(2);
        var descriptions = await db
            .SpendEntries.Select(e => e.Description)
            .ToListAsync(TestContext.Current.CancellationToken);
        descriptions.Should().BeEquivalentTo("Actions Windows", "Code Quality AI Credits");
    }

    /// <summary>
    /// The update half of the same guarantee. A failed UPDATE leaves the entity tracked as
    /// Modified, so without the detach the very next line's save retries it, fails again, and
    /// one bad row takes every row after it — which is precisely what saving per line exists
    /// to prevent.
    /// </summary>
    [Fact]
    public async Task ARejectedUpdateDoesNotPoisonTheLinesAfterIt()
    {
        var interceptor = new RejectSkuInterceptor("Actions Linux");
        await using var db = NewDb(seedCatalog: true, interceptor);

        // First run writes both rows with the interceptor disarmed, so there is an existing
        // row for the second run to try (and fail) to update.
        interceptor.Armed = false;
        await Create(
                db,
                ClientReturning(
                    Item("actions", "Actions Linux", 10m),
                    Item("code_quality", "Code Quality AI Credits", 30m)
                ),
                FxAt(1m)
            )
            .SyncAsync(TestContext.Current.CancellationToken);
        interceptor.Armed = true;

        var written = await Create(
                db,
                ClientReturning(
                    Item("actions", "Actions Linux", 999m),
                    Item("code_quality", "Code Quality AI Credits", 60m)
                ),
                FxAt(1m)
            )
            .SyncAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        var codeQuality = await db
            .SpendEntries.AsNoTracking()
            .SingleAsync(
                e => e.Description == "Code Quality AI Credits",
                TestContext.Current.CancellationToken
            );
        codeQuality
            .Amount.Should()
            .Be(60m, "the line after the rejected one must still reach the ledger");
        var actions = await db
            .SpendEntries.AsNoTracking()
            .SingleAsync(
                e => e.Description == "Actions Linux",
                TestContext.Current.CancellationToken
            );
        actions.Amount.Should().Be(10m, "the rejected update must not have been persisted");
    }

    [Fact]
    public async Task DerivesAnEntryKeyFromTheMonthProductAndSku()
    {
        await using var db = NewDb();
        var sut = Create(db, ClientReturning(Item("actions", "Actions Linux", 10m)), FxAt(0.8m));

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry
            .EntryKey.Should()
            .Be(
                "github:2026-07:actions:Actions Linux",
                "the key excludes the amount, which is the point — the amount is what changes as a month accrues"
            );
    }

    [Fact]
    public async Task TruncatesALongSkuSoTheWriteStillSucceeds()
    {
        await using var db = NewDb();
        var sut = Create(
            db,
            ClientReturning(Item("actions", new string('x', 300), 10m)),
            FxAt(0.8m)
        );

        await sut.SyncAsync(TestContext.Current.CancellationToken);

        var entry = await db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry.Description.Should().HaveLength(200, "Description is varchar(200)");
        entry.EntryKey.Should().HaveLength(200, "EntryKey is varchar(200)");
    }

    /// <summary>
    /// Fails the save for one SKU, so the per-line recovery path can be exercised. Disarm it to
    /// seed rows that a later, armed run then tries to update.
    /// </summary>
    private sealed class RejectSkuInterceptor(string skuToReject) : SaveChangesInterceptor
    {
        public bool Armed { get; set; } = true;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result
        )
        {
            var offending =
                Armed
                && eventData
                    .Context!.ChangeTracker.Entries<SpendEntry>()
                    .Any(e =>
                        e.State is EntityState.Added or EntityState.Modified
                        && e.Entity.Description == skuToReject
                    );

            return offending
                ? throw new DbUpdateException("rejected by test interceptor")
                : base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(SavingChanges(eventData, result));
    }
}
