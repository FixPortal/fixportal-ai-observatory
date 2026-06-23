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
