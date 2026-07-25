# Anthropic cost correction — plan (rev 2, post-audit)

Repo: `D:\fix-portal\fixportal-ai-observatory` (public, org FixPortal).

**Rev 2 supersedes rev 1**, which was audited by a five-reviewer / four-vendor
adversarial panel on 2026-07-25 (run `20260725T084848Z`; report in the Obsidian
vault under `Claude/Adversarial Review/fixportal-ai-observatory/`). The audit
found rev 1 unbuildable as written. This revision records what changed and why.

The single most important change: **rev 1 assumed the stored token counts were
sound and only the rates were wrong. They are not.** The re-cost rev 1 proposed
would have carefully repriced corrupted numbers.

---

## 1. The blocking defect: the sweeper counts every message several times

`~/.claude/hooks/backfill/observe-sweep.ps1:480-491` accumulates usage per
**transcript line**. Claude Code re-emits the same assistant message across
multiple lines while streaming, so a message's usage is counted once per line it
appears on. The comment on line 478 — *"Sum each assistant turn once"* — states
the intent, but the loop keys on nothing.

Measured over the 40 most recently written transcripts: 5,103 usage-bearing
lines carrying **2,256 unique `message.id` values**.

| Token class | Raw per-line | Deduplicated | Inflation |
|---|---:|---:|---:|
| input | 120,955 | 44,290 | 2.73x |
| output | 5,640,704 | 1,969,148 | 2.86x |
| cache write | 45,635,308 | 16,982,439 | 2.69x |
| cache read | 1,250,679,774 | 567,464,414 | 2.20x |

**Every Anthropic token count in the database is overstated by roughly
2.2–2.9x**, and therefore so is every Anthropic cost figure — independently of,
and on top of, the pricing-table errors rev 1 set out to fix.

### The deduplication rule, determined empirically

Sampling 25 transcripts, 1,428 of 2,007 message ids appear more than once:

| Duplicate-group shape | Count |
|---|---:|
| identical across all lines | 1,279 |
| monotonically growing | 149 |
| neither | 0 |

Growing groups are partial-then-final streamed records (e.g. `out=5` then
`out=179`, the other three fields unchanged). Therefore:

> **Group by `message.id` within a transcript and take the MAXIMUM of each token
> field** (equivalently the last occurrence — no group was non-monotonic).

First-seen deduplication is **wrong**: it undercounts output on the 149 growing
groups. This is the one rule the fix must get right.

---

## 2. Corrected premises

Rev 1's premises P1–P12 were audited individually. Most held. These changed:

| # | Rev 1 claim | Status after audit |
|---|---|---|
| P8 | "Re-cost is a pure function of already-stored columns" | **FALSE.** The stored columns are inflated (§1), and the cache-write TTL that determines the applicable rate was discarded at ingest (`observe-sweep.ps1:489` keeps only `cache_creation_input_tokens`). |
| P9 | History unreachable before 2026-06-25, from `LastWriteTime` | **METHOD UNSOUND.** File modification time is not earliest-event time; a file appended after that date can hold older records. Direction is favourable (more history may be recoverable), but the number must be measured from event timestamps, not file metadata. |
| P10 | `$159,614 -> $83,515`, factor 0.52 | **VOID.** Computed from inflated counts, at list rather than intro Sonnet-5 rates, over six exact-matched model keys rather than the resolver's prefix matching. The true figures are not currently known by anyone. |
| P10 | "driven by cache-read priced 1.50 vs 0.50" | **UPHELD** — and challenged wrongly. Three reviewers called it a misattribution; the adjudicator settled it on data: cache-read is **86%** of the Opus reduction (`cache_read : output` ≈ 216:1). |
| P12 | Sweeper event-key format | **TRUE**, confirmed at `observe-sweep.ps1:526`; rev 1 cited files that did not show it. |

P1–P7 and P11 stand as written.

## 3. Verified facts (rev 1 asserted these without a source)

Rev 1 listed the Anthropic rates as unverified assumptions A1–A3 and then quoted
a precise total as if established. Three reviewers independently fetched
<https://platform.claude.com/docs/en/about-claude/pricing> and every rate in
`src/AiObservatory.Ingest/appsettings.json:10-27` matches exactly, including the
Sonnet-5 intro window as a dated pair.

Verified 2026-07-25. Per million tokens:

| Model | in | out | 5m write | **1h write** | read |
|---|---:|---:|---:|---:|---:|
| Opus 4.5 / 4.6 / 4.7 / 4.8 / 5 | 5 | 25 | 6.25 | **10** | 0.50 |
| Sonnet 5 (to 2026-08-31) | 2 | 10 | 2.50 | **4** | 0.20 |
| Sonnet 5 (from 2026-09-01) | 3 | 15 | 3.75 | **6** | 0.30 |
| Sonnet 4 / 4.5 / 4.6 | 3 | 15 | 3.75 | **6** | 0.30 |
| Haiku 4.5 | 1 | 5 | 1.25 | **2** | 0.10 |
| Fable 5 / Mythos 5 | 10 | 50 | 12.50 | **20** | 1.00 |

**The cache-write TTL problem.** `appsettings.json` carries the 5-minute column
throughout. Local transcripts contain **zero** `ephemeral_5m_input_tokens` and
non-zero `ephemeral_1h_input_tokens` across every model — this deployment writes
1-hour cache entries exclusively, which bill at 2x base input, not 1.25x. The
split is recorded in the transcripts but discarded by the sweeper, so it cannot
be recovered from the database.

## 4. Findings that reshape the design

Full detail in the audit report; these are the ones that change the work.

- **C1 (Critical)** — Aggregate-only re-cost breaks an invariant the code
  already defends. `PatchEventCostAsync` (`UsageRepository.cs:166-222`) derives
  `delta = newCost - UsageEvent.CostUsd` and applies it to `DailyAggregates`.
  Re-cost only the aggregate and the next legitimate cost patch silently
  corrupts the corrected row; the `rowsAffected != 1` guard cannot catch it —
  one row, wrong amount. **Q1 is answered: both levels, one transaction.**
- **H2 (High)** — `CopilotUsageClient.cs:23` calls `/orgs/{org}/copilot/metrics`,
  which GitHub retired on **2 April 2026**. The README section added alongside
  rev 1 asserts this arm "is correct" — that is false and must be corrected.
- **H3 (High)** — No audit trail. `DailyAggregate` has one `CostUsd` column and
  no prior-value, version, or run record. Apply is irreversible and
  unattributable.
- **H4 (High)** — The proposed endpoint has no provider scoping, while the
  `DELETE` it mirrors is explicitly provider-scoped and the resolver has a
  non-null fallback that would reprice OpenAI/Copilot/Google rows at Anthropic
  rates.
- **H5 (High)** — There is a **fourth** pricing table:
  `src/AiObservatory.Web/src/config/providers.ts:19-27` holds
  `cacheSavingsPerToken` at pre-correction rates, rendered live at
  `SummaryCards.tsx:103`. 3x overstated for current Opus, no date dimension.
- **M6 (Medium)** — The README's "neither API exposes subscription usage" is
  overbroad: `/v1/organizations/usage_report/claude_code` reports
  `customer_type: subscription`. Bounded — it needs an org Admin key and covers
  only Team/Enterprise, so the decision stands; the stated reason does not.
- **M7 (Medium)** — `observe-sweep.ps1` defines two pricing tables with
  **transposed** cache columns (`$copilotPricing` is read/write;
  `$anthropicPricing` is write/read). Copying a row between them swaps 6.25 and
  0.50 — a 12.5x error with no failure signal.

## 5. Revised design, in dependency order

Rev 1's ordering was wrong: it corrected rates first. Rates are the *last* thing
that matters, because the counts beneath them are broken.

**Stage 1 — fix the counts (blocking).**
1. Deduplicate by `message.id`, taking the max per field (§1). Add a check that
   fails loudly if a group is ever non-monotonic.
2. Capture the cache-write TTL split (`ephemeral_5m_input_tokens` /
   `ephemeral_1h_input_tokens`) instead of discarding it, and carry it through
   `POST /api/events`.

**Stage 2 — re-derive the numbers.** Only once Stage 1 lands. Report actual
inflation and actual corrected totals through the real resolver, not a
hand-rolled script with exact-match keys.

**Stage 3 — pricing single-source.** Adopt the alternative every repo-aware
reviewer reached independently: **compute Anthropic cost server-side at ingest.**
`POST /api/events` already receives model, tokens and `occurredAtUtc`, so the API
can call the one C# resolver and the sweeper deletes its pricing entirely — no
fetch endpoint, no cache, no fallback table, no PowerShell resolver. This is
strictly smaller than rev 1's fetch-from-API design and removes M3 and M7
outright.
- Move `AnthropicPricingOptions` / `PricingEntry` / `PricingRates4` to
  `AiObservatory.Data` (both apps already reference it).
- Extract the resolver from `AnthropicUsageClient.ComputeCost` with a `[Theory]`
  covering longest-prefix, date windows, dated-beats-undated, and fallback
  behaviour.
- Add the 1-hour cache-write rate to `PricingEntry`.
- Fix `providers.ts` (H5) or scope it out explicitly — but name it either way.

**Stage 4 — re-cost, if still wanted.** Provider-scoped (H4), both levels in one
transaction (C1), audit trail and pricing-version stamp first (H3), dry-run
default. Decide against the Stage 2 numbers, not rev 1's.

**Stage 5 — README corrections.** Copilot arm retired (H2); Anthropic claim
qualified (M6). Independent of the rest; can ship first.

## 6. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | Stage 1 changes historical token counts as well as costs — a bigger restatement than rev 1 contemplated | Audit trail (H3) before any write; annotate the discontinuity |
| R2 | The 1h/5m split cannot be recovered for existing rows | Either backfill from transcripts where they survive, or price history explicitly as all-1h and document it |
| R3 | Re-cost concurrent with live ingest | One transaction at RepeatableRead or better; version-stamp each run |
| R4 | Stale `Insight` rows quote pre-correction figures behind a watermark that prevents re-analysis | Annotate, or regenerate for the affected window |

## 7. Open questions

- **Q1** — ~~Re-cost `UsageEvent` too?~~ **Answered: yes, mandatory, atomic (C1).**
- **Q2** — `$copilotPricing`'s Claude rows: given H2, is deleting the Copilot
  token-pricing rows the right answer rather than correcting them?
- **Q3** — Is the corrected history worth keeping at all, or is a clean cut
  (annotate and move on) better than a restatement of numbers that were wrong in
  two independent ways?

## 8. Explicitly out of scope

- The actual-cost / billed-spend feature (separate design pass).
- Re-enabling either ingest arm. Note the README claims about them are being
  corrected (H2, M6) — "inert by design" survives for Anthropic on a personal
  plan, but not the claim that the Copilot code is correct.
