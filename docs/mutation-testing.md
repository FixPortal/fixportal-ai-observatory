# Mutation testing

Stryker.NET runs nightly against the API (`.github/workflows/mutation.yml`,
`stryker-config.json`). The score is informational (`break: 0`); execution and report
failures are real failures.

This document exists because the setup looks over-specified and is not. Every constraint
below was established by measurement after the nightly run failed for three consecutive
days in July 2026, and three plausible fixes shipped without changing the outcome.

The run takes **about 40 seconds**. If it ever takes minutes, something below has regressed.

## Run Stryker from the unit test project directory

This is the single most important line in the workflow:

```yaml
working-directory: tests/AiObservatory.Api.Tests
run: dotnet stryker --config-file ../../stryker-config.json
```

**Stryker's `test-projects` config key does not restrict execution.** Run from the
repository root and Stryker discovers *every* test project that references
`AiObservatory.Api` — including `AiObservatory.Api.IntegrationTests` — and runs all of them
against every mutant, whatever `test-projects` says. Run from the unit test project and it
uses that project alone.

The difference, same config otherwise:

| Invocation | Tests in the lane | Runtime |
|---|---|---|
| From repository root | 246 | >55 min (timed out) |
| From `tests/AiObservatory.Api.Tests` | 133 | ~40s |

The one-line check is Stryker's own startup log:

```
[INF] Number of tests found: 133 for project .../AiObservatory.Api.csproj.
```

**246 means this has regressed.** Check it first whenever the run slows down.

## The test-project split is what makes that possible

`AiObservatory.Api.Tests` is unit-only. Everything that boots a host via
`WebApplicationFactory` or touches PostgreSQL lives in `AiObservatory.Api.IntegrationTests`.

This was previously attempted with `test-case-filter: "Category!=Integration"`. That does
not work: **`test-case-filter` is implemented only in Stryker's VSTest runner.** In Stryker
4.16.0's source, `TestCaseFilter` appears solely under `src/Stryker.TestRunner.VsTest/**`;
the MTP runner project has no reference to it. The config parses, the run proceeds, no
warning is emitted, and the filter does nothing. The traits are still correct for the
`dotnet test` lanes — they are simply not load-bearing for Stryker, and must never be
relied on to be.

Two guards enforce the split, because a convention would erode:

- `ArchitectureTests.Unit_test_project_must_not_reference_database_or_host_packages` fails
  the build if the unit project gains `Mvc.Testing`, `Npgsql` or `Testcontainers`.
- The mutation job runs with **no PostgreSQL service and no `TEST_DB_CONNECTION`**, so a
  database-backed test landing in the unit project fails immediately and visibly rather
  than costing an hour a night.

Note `tests/PostgresTestAssemblyFixture.cs` is an xUnit **assembly** fixture: it starts a
Testcontainers PostgreSQL when `TEST_DB_CONNECTION` is unset, and assembly fixtures run
whatever the test filter says. It is linked into the integration project only, and must
stay that way.

## `test-runner` must be `mtp`

`vstest` looks attractive because it honours `test-case-filter`. It produces **invalid
results** here: against xunit.v3 built as `OutputType=Exe`, Stryker's VSTest runner executes
the tests but never attributes a failure to a mutant.

Measured on `InsightResponseParser`, which has dedicated unit tests: `vstest` killed **0 of
19** mutants. On the full scoped set it reported `Killed: 0, Survived: 224, Timeout: 194` —
and a "46.41%" score that was purely the timeouts, since Stryker counts a timeout as a kill.
Its coverage capture fails under the same runner (`It looks like the test coverage capture
failed`) for what is presumably the same reason.

A slow correct answer beats a fast wrong one; here `mtp` is both correct and fast.

## `coverage-analysis` is `perTest`

Correct *given* the split, and worth understanding, because it was measured as catastrophic
before it:

| Configuration | Per mutant |
|---|---|
| `mtp`, `perTest`, mixed unit + integration lane | ~39s |
| `mtp`, `perTest`, unit-only lane | ~0.4s |

`perTest` runs only the tests that **cover** each mutant. While the integration tests were
in the lane, the tests covering an endpoint mutant were exactly the expensive
`WebApplicationFactory` ones, so coverage analysis selected the worst possible set. With a
unit-only lane it selects a handful of fast tests, which is what it is for.

## `mutate` is scoped, on purpose

Mutating the whole API produced 1988 mutants. Most bought nothing: a surviving mutant in DI
wiring, a dashboard read endpoint or a prompt builder does not change an engineering
decision. The globs cover the surfaces where a silent wrong answer costs money or leaks
data — FX conversion, billed spend and its idempotency keys, the ledger write path, and the
auth filters.

**Expect a low score, and read it correctly.** It is ~19%, against ~49% when the whole API
was mutated with integration tests in the lane. That is not a regression in test quality;
those two numbers measure different things. Most of the scoped mutants now report
`NoCoverage` — the money paths are covered by integration tests, which are deliberately not
in this lane. The honest reading is "the unit tests do not exercise the money paths", which
is worth knowing and was previously hidden behind integration coverage.

## Reading a slow run

Total cost is `mutants x tests-per-mutant x test cost`, and only the first is visible in a
timeout. Establish, in order:

1. **Tests per mutant** — the `Number of tests found` line. This is the one that fails
   silently.
2. **Per-mutant cost, measured** — run Stryker locally with `mutate` narrowed to one small
   directory. It finishes in minutes and gives seconds-per-mutant directly.
3. **Mutant count** — `N total mutants will be tested`.

Multiply before concluding. Skipping to (3) is how three separate fixes shipped against this
workflow without changing the outcome — each addressing a genuine defect, none of them the
dominant term.
