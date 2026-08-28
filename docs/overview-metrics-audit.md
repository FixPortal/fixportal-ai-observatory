# Overview metrics audit

This is the canonical definition and reconciliation record for the Overview cards. Update it when a card's calculation, source authority, or audit result changes. Never include credentials or private raw payloads.

## Billed spend

### Definition

`Billed spend` is the sum of signed `SpendEntries.AmountGbp` values whose `OccurredOn` date falls inside the Overview's rolling 31-calendar-day window, including both endpoints.

- The end date is the browser's local calendar date when the Overview mounts.
- The start date is 30 calendar days before the end date.
- A charge contributes positively; a refund or credit contributes negatively.
- Foreign-currency rows use the GBP amount and FX rate frozen when the ledger row was recorded or corrected.
- The card includes rows whose cost basis is billed. It excludes token-rate estimates, provider estimates, and subscription notional value.
- The reporting endpoint groups directly over the complete ledger query. It does not use the capped ledger-list response.

Code lineage:

- Window: [`src/AiObservatory.Web/src/api/queries.ts`](../src/AiObservatory.Web/src/api/queries.ts)
- Card: [`src/AiObservatory.Web/src/components/SummaryCards.tsx`](../src/AiObservatory.Web/src/components/SummaryCards.tsx)
- Aggregate: [`src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs`](../src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs)
- Frozen GBP values: [`src/AiObservatory.Data/Entities/SpendEntry.cs`](../src/AiObservatory.Data/Entities/SpendEntry.cs)

### Reconciliation procedure

For the dates displayed by the card:

1. Read `GET /api/spend/reporting?from=yyyy-MM-dd&to=yyyy-MM-dd`.
2. Read `GET /api/spend/entries?from=yyyy-MM-dd&to=yyyy-MM-dd&limit=5000`. If `entryCount` exceeds the returned row count, retrieve smaller date slices; never reconcile a capped response as though it were complete.
3. Confirm all four values agree at ledger precision:
   - `reporting.totalGbp`;
   - the sum of raw `amountGbp` rows;
   - the sum of `dailySeries.amountGbp`;
   - the sum of `vendorSeries.amountGbp`.
4. Group raw rows by `sourceId`, vendor, category, currency, and entry-key identity. Arithmetic agreement proves only internal consistency; it does not prove that two rows are not the same supplier charge.
5. Reconcile each acquisition lane to its authority:
   - `portal`: matching Tax Portal expense IDs and the feed's VAT basis;
   - `github-billing-api`: GitHub enhanced billing usage, using net amounts;
   - other provider sources: their retained `BillingObservation` identities and upstream billed export or API.
6. Record unresolved overlaps, freshness differences, missing source access, and whether supplier invoices or receipts were inspected.

### Audit — 2026-08-28

Window: **2026-07-29 through 2026-08-28**, inclusive.

The card displayed **£1,133.87** from 25 ledger rows. Its unrounded value, raw-row sum, daily-series sum, and vendor-series sum all agreed at **£1,133.8723** with a zero arithmetic delta.

Source composition:

| Source | Rows | GBP | Reconciliation |
| --- | ---: | ---: | --- |
| Tax Portal expenses | 8 | 810.6400 | All eight expense IDs matched the Tax Portal source amounts and VAT basis. |
| Canonical GitHub provider observations | 9 | 166.1696 | Matched the retained provider-feed snapshot. |
| Migrated legacy GitHub snapshots | 8 | 157.0627 | Every row had a canonical counterpart and was counted a second time. |
| **Displayed total** | **25** | **1,133.8723** | Arithmetically consistent but financially overstated. |

The internally corrected value at the same Observatory snapshot was **£976.8096**, displayed as **£976.81**. A direct GitHub API check later in the audit had advanced by £0.0638 at the same stored FX rate, producing a then-current comparison of **£976.8734**; that difference was source freshness, not another ledger discrepancy.

The duplicate lineage was:

1. The original GitHub billing sync wrote deterministic `github:<month>:<product>:<sku>` API ledger rows.
2. `20260824172007_AddObservationProvenance` conservatively labelled every pre-existing spend row `legacy-spend` rather than guessing its origin.
3. The retained-observation writer introduced canonical `github-billing-api` rows but originally adopted an old key only when its row already carried the new source ID. The migrated production rows therefore did not match and new rows were inserted.
4. Production held 18 legacy GitHub rows from May through August, totalling £451.6050. All 18 had canonical counterparts; no unpaired legacy GitHub row was found. Closed months May, June, and July matched their canonical counterparts exactly. August's stale snapshots differed because the canonical month continued accruing.

Remediation:

- [`20260828082356_RemovePairedLegacyGitHubSpend.cs`](../src/AiObservatory.Data/Migrations/20260828082356_RemovePairedLegacyGitHubSpend.cs) deletes only API/`legacy-spend` GitHub rows whose canonical `github-billing-api` counterpart exists.
- [`BillingObservationWriter.cs`](../src/AiObservatory.Data/Spend/BillingObservationWriter.cs) adopts an unpaired migrated GitHub row on first retained observation, preventing recurrence when an older database is upgraded.
- Portal and genuinely unpaired legacy spend are retained.
- This audit reconciled the Observatory to Tax Portal source records and GitHub's billing API. It did not independently inspect every supplier invoice or receipt.
