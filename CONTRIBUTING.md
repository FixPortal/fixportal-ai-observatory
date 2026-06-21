# Contributing

Thanks for your interest in improving this project. It is maintained on a
best-effort basis; issues and pull requests are welcome.

## Ground rules

- Be civil. This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
- By contributing, you agree your contributions are licensed under the
  [Apache License 2.0](LICENSE), the same licence as the project.
- Open an issue before a large change so we can agree the approach before you
  invest the time.

## Getting set up

Prerequisites: **.NET 10 SDK**, **Node 22+**, and **PostgreSQL 16** (or Docker).
See the [README](README.md#local-development) for the full local setup,
environment variables, and EF Core migration steps.

```bash
git clone https://github.com/FixPortal/fixportal-ai-observatory.git
cd fixportal-ai-observatory
# backend
cd src/AiObservatory.Api && dotnet run
# frontend (separate shell)
cd src/AiObservatory.Web && npm install && npm run dev
```

## Before you open a PR

Run the full local check — CI runs the same and must pass before a merge:

```bash
dotnet test                              # all .NET tests
cd src/AiObservatory.Web && npm test     # Vitest unit tests
npm run doctor                           # React Doctor diagnostics
```

The .NET tests need a PostgreSQL instance; the README shows a one-line Docker
command to spin one up.

## Branches and commits

- Branch from `main` using `feat/<scope>`, `fix/<scope>`, or `chore/<scope>`.
- Write clear, present-tense commit subjects.
- PRs merge via **rebase** — no merge commits, no squash. Keep your branch
  rebased on `main`.

## What makes a good PR

- One focused change per PR.
- Tests for new behaviour or a bug fix that would have caught the regression.
- No new runtime dependency unless a few lines of code genuinely cannot do it.
- Database schema changes ship with an EF Core migration in the same PR.
