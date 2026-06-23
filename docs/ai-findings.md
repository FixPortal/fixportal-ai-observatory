# AI Findings Ledger

Durable record of un-dismissable static-analysis findings (GitHub Code Quality,
CodeQL, Copilot AI Findings). Substitutes for the missing dismiss UI so the same
by-design issues do not get re-investigated on each scan.

| Finding | Status | Reason | Rationale | First seen |
|---|---|---|---|---|
| `src/AiObservatory.Web/src/components/DateRangePicker.tsx:47-48` — react-doctor/rerender-lazy-state-init | fixed | | Wrapped `toISOString()` calls in arrow-function initialisers so they run on mount only, not every render (PR #35) | 2026-06-23 |
| `src/AiObservatory.Web/package.json` — npm audit high: undici 7.0.0–7.27.2 (TLS bypass, cache poisoning, header injection, DoS) | fixed | | `npm audit fix` bumped undici past the vulnerable range (PR #35) | 2026-06-23 |
| `src/AiObservatory.Web/src/components/DateRangePicker.tsx:43` — react-doctor/prefer-useReducer (5 useState calls) | dismissed | by-design | Component uses the recommended prev-value tracking pattern (lines 53–60) to sync props→state without effects. The 5 state slots are independently updated; grouping into a reducer would obscure the derived-state guard without reducing complexity. | 2026-06-23 |
| `src/AiObservatory.Web/src/design/index.ts` — react-doctor/bundle-barrel-imports (bypassed barrel) | fixed | | All 8 consumer sites updated to direct imports; barrel file deleted (commit 4bd9251). Score 97→100. | 2026-06-23 |
| `src/AiObservatory.Web/src/components/SpendChart.tsx` — unstable Inner component identity inside lazy() factory | fixed | | Hoisted Inner to module scope via .then(); chart no longer remounts on mode/date toggle. | 2026-06-23 |
| `src/AiObservatory.Web/src/components/ProviderSplit.tsx` — unstable Inner component identity inside lazy() factory | fixed | | Hoisted Inner to module scope via .then(); chart no longer remounts on parent state changes. | 2026-06-23 |
| `src/AiObservatory.Web/src/pages/Dashboard.tsx` — accessibility/landmark-one-main: no <main> landmark | fixed | | Wrapped primary content region in <main class="dashboard__main">. | 2026-06-23 |
| `src/AiObservatory.Web/src/components/SummaryCards.tsx:71` — useMemo wrapping primitive .filter().length | fixed | | Dropped useMemo; inline filter().length is cheap enough not to memoize. | 2026-06-23 |
| `src/AiObservatory.Web/src/components/ModelBreakdown.tsx:160` — Math.max(...arr.map()) spread | fixed | | Replaced with arr.reduce() to avoid argument-count blowup on large datasets. | 2026-06-23 |
| `src/AiObservatory.Web/public/robots.txt` — SEO/robots-txt: no robots.txt | fixed | | Created public/robots.txt with Disallow: / (internal dashboard, must not be indexed). | 2026-06-23 |
| `src/AiObservatory.Web/src/components/SpendChart.tsx` — byDate useMemo includes mode dep: full reduce re-runs on chart toggle | open | | Split-memo opportunity (separate reduce from normalise pass) but micro-optimisation; data volume is small. Review if dataset grows. | 2026-06-23 |
| `src/AiObservatory.Web/src/components/SubscriptionPanel.tsx:178` — aggregates.filter() inside .map(): O(n*m) | open | | Build a per-provider Map instead; not a problem at current subscription count (<=5), revisit if provider cardinality grows. | 2026-06-23 |
| `src/AiObservatory.Web/src/components/*.tsx` — .sort() on fresh arrays vs .toSorted() idiom (BudgetRulesPanel, ModelBreakdown, SpendChart, SubscriptionPanel) | open | | Minor idiom preference; .sort() on a freshly-created array is safe. No correctness risk, defer to a style sweep. | 2026-06-23 |
| `src/AiObservatory.Web/src` — accessibility/color-contrast: Lighthouse flagged at least one element | open | | Candidate is --text-faint (#94a3b8) or --text-muted (#6b7280) on small text. Exact element unconfirmed without a live Lighthouse run. Investigate with DevTools before changing tokens. | 2026-06-23 |
