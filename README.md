![Build](https://github.com/FixPortal/fixportal-ai-observatory/actions/workflows/ci.yml/badge.svg)
![License](https://img.shields.io/github/license/FixPortal/fixportal-ai-observatory)

# AI Observatory

> OSS observability for AI usage and cost evidence, as of 2026-08-25. It keeps billed spend, public-list estimates, subscription notional values, and missing data distinct.

AI Observatory is a .NET 10 and React 19 dashboard with a PostgreSQL store. Provider sources, local CLI telemetry, and manual entries retain provenance so unlike evidence is never silently merged.

## Read these first

- [Provider setup](docs/provider-setup.md) — every source, access requirement, and known unavailable capability.
- [Truth and pricing](docs/truth-and-pricing.md) — source/scope/basis meanings and safe catalog refresh.
- [Adding a provider](docs/adding-a-provider.md) — the compile-time adapter seam.
- [Local producers](clients/README.md) — Codex, Copilot, Claude, and Kimi sweeper setup.
- [Postman collection](docs/ai-observatory.postman_collection.json) — representative authenticated API requests.

## Local development

### Quick start

> [!WARNING]
> Restore depends on private FixPortal GitHub Packages. Only contributors explicitly granted package access can build; public users without it cannot complete this quick start. This is an unresolved OSS release blocker.

Authorized contributors can set their package-read token before Docker or manual restore:

```powershell
$env:GITHUB_PACKAGES_TOKEN = '<github-packages-token>'
```

```powershell
docker compose up --build
```

This starts PostgreSQL, the API, and the frontend at [http://localhost:4173](http://localhost:4173). The compose seed populates sample data; it is for local exploration, not a representation of provider billing.

For a manual run, install .NET SDK 10, Node 24+, and PostgreSQL 16. Configure `DB_CONNECTION` and `OBSERVATORY_API_KEY` through user secrets or environment variables, then start the API and web app:

```powershell
dotnet restore AiObservatory.slnx
```

```powershell
npm --prefix src/AiObservatory.Web ci
```

```powershell
dotnet run --project src/AiObservatory.Api
```

```powershell
npm --prefix src/AiObservatory.Web run dev
```

The ingest worker is optional and only activates sources whose required settings are present:

```powershell
dotnet run --project src/AiObservatory.Ingest
```

Use neutral placeholders such as `<observatory-api-key>` outside your secret store. See [Provider setup](docs/provider-setup.md) for acquisition settings; pricing catalogs for OpenAI, Claude, and Kimi refresh without credentials, while Google catalog pricing remains known unavailable until verified SKU mappings exist.

## Dashboard truth

- Billed spend contains provider-reported financial or ledger evidence only.
- Estimated cost is API usage rated from an observed public catalog.
- Subscription notional value is a separate comparison, not spend.
- Missing money or tokens read `Not reported`, never zero.
- Source status shows configuration, freshness, failure, and unavailability separately from process liveness.

Supported acquisition includes OpenAI usage/costs, Anthropic usage/cost reports and optional Claude Code analytics, GitHub Copilot organization engagement, Google Cloud Billing BigQuery export, GitHub activity/billing, and local Codex/Copilot/Claude/Kimi telemetry. The [provider matrix](docs/provider-setup.md) is the authoritative capability list.

## API

Requests use `X-Observatory-Key` when API keys are configured. The most useful entry points are:

| Method | Route | What it provides |
| --- | --- | --- |
| `GET` | `/api/aggregates` | Daily source-aware usage and cost aggregates |
| `GET` | `/api/sources/status` | Source configuration, freshness, and sanitized status |
| `POST` | `/api/events` | A provenance-labelled usage event |
| `GET` | `/api/subscriptions` | Subscription ledger records |
| `GET` | `/api/insights` | Generated insights |

Import the [Postman collection](docs/ai-observatory.postman_collection.json), set `base_url` and `api_key`, and use its source-aware event examples. It is a representative collection, not an exhaustive API specification.

## Contributing

Run the focused local-producer check:

```powershell
node --test clients/observatory-sweep.test.mjs
```

Run the solution checks before a pull request:

```powershell
dotnet test --solution AiObservatory.slnx --configuration Release
```

```powershell
npm --prefix src/AiObservatory.Web test -- --run
```

The repository is [Apache-2.0](LICENSE) licensed. Keep provider changes source-aware and update the setup matrix with any new capability or limitation.

- [Contributing](CONTRIBUTING.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)
- [Security policy](SECURITY.md)

## Troubleshooting

| Symptom | Cause and next step |
| --- | --- |
| A provider is `Not configured` | Supply the exact required settings and upstream access in [Provider setup](docs/provider-setup.md). |
| Google catalog stays unavailable | This is expected while verified SKU mappings are empty; billed BigQuery export remains independent. |
| Claude activity appears twice | Exclude local Claude telemetry when Claude Code Analytics covers the same activity. |
| A cost is missing | Required model dimensions may be unknown; Observatory intentionally returns no guessed fallback price. |
