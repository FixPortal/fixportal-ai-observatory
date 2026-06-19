# AI Observatory

Full-stack observability dashboard for AI service spending. Tracks token usage and costs across Anthropic, Google, and GitHub Copilot with AI-generated insights, subscription management, and budget alerting.

**Live:** [observatory.fixportal.org](https://observatory.fixportal.org) | **API:** [fpaiobs-api.azurewebsites.net](https://fpaiobs-api.azurewebsites.net)

---

## What It Does

- Aggregates daily token usage and costs per provider and model
- Displays a 14-day spend trend chart and provider breakdown
- Runs a background intelligence worker that generates anomaly, efficiency, recommendation, and summary insights via the Anthropic SDK
- Manages subscriptions (monthly flat cost + extra usage overlay, progress bar vs. period spend)
- Supports budget rules and FX-rate-aware GBP display

Usage events enter via a `POST /api/events` endpoint, currently fed by a Claude Code `Stop` hook on the developer's machine.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Frontend | React 19, TypeScript 6, Vite 8 |
| Charts | Recharts 3 (lazy-loaded) |
| Server state | TanStack React Query 5 |
| Styling | Custom CSS + `@fixportal/design` tokens (no Tailwind) |
| Backend | ASP.NET Core 10, Minimal APIs |
| ORM | EF Core 10 + NodaTime |
| Database | PostgreSQL 16 |
| AI insights | Anthropic SDK v5 (`claude-*` models) |
| Hosting | Azure Static Web App (frontend), Azure App Service F1 (API) |
| IaC | Bicep (in `infra/`) |
| Observability | Azure Application Insights |

---

## Project Structure

```
fixportal-ai-observatory/
├── .github/
│   └── workflows/
│       ├── ci.yml              # Build & test (PR + main)
│       ├── deploy.yml          # Release to Azure (triggered by CI on main)
│       ├── infra.yml           # Bicep deploy (triggered on infra/** changes)
│       └── react-doctor.yml    # React diagnostics on PRs
├── infra/
│   ├── main.bicep
│   └── modules/
│       ├── appservice.bicep    # fpaiobs-api App Service
│       ├── postgresql.bicep    # fpaiobs-db managed PostgreSQL
│       ├── keyvault.bicep      # fpaiobs-kv secrets
│       ├── appinsights.bicep   # Application Insights
│       └── swa.bicep           # fpaiobs-swa Static Web App
├── src/
│   ├── AiObservatory.Api/      # ASP.NET Core 10 Minimal API
│   ├── AiObservatory.Data/     # EF Core entities, DbContext, migrations
│   └── AiObservatory.Web/      # React 19 frontend
├── tests/
│   ├── AiObservatory.Api.Tests/
│   └── AiObservatory.Data.Tests/
└── AiObservatory.slnx
```

---

## API Endpoints

All routes are under `/api`.

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/aggregates` | Daily token usage + cost by provider/model (default: last 14 days) |
| `GET` | `/api/insights` | 50 most recent AI-generated insights |
| `POST` | `/api/insights/{id}/acknowledge` | Mark an insight as read |
| `GET` | `/api/subscriptions` | List billing subscriptions |
| `POST` | `/api/subscriptions` | Create a subscription |
| `PUT` | `/api/subscriptions/{id}` | Update a subscription |
| `PATCH` | `/api/subscriptions/{id}/extra-usage` | Set extra usage cost override |
| `DELETE` | `/api/subscriptions/{id}` | Remove a subscription |
| `GET` | `/api/budget-rules` | List budget alert rules |
| `POST` | `/api/events` | Ingest a raw usage event |

Requests to the API require an `X-Observatory-Key` header matching the `OBSERVATORY_API_KEY` secret.

---

## Frontend Architecture

The frontend (`src/AiObservatory.Web`) is a single-page app served by Azure Static Web App. It calls the API directly (cross-origin — there is no /api proxy on the free SWA tier).

### Code splitting

- React and Recharts ship in separate vendor chunks
- `SpendChart` and `ProviderSplit` are wrapped in `React.lazy()` + `Suspense` so Recharts is never in the initial bundle

### Design system

The app imports `@fixportal/design/tokens.css` (shared monorepo package) for universal surface, text, brand, status, and border tokens. It never redefines those tokens. App-local tokens (provider palette, spacing scale, radius) live in `src/index.css`.

Provider colours are categorical, not semantic:

| Provider | Light | Dark |
|---|---|---|
| anthropic | `#7c3aed` (violet) | `#a78bfa` |
| google | `#0284c7` (sky) | `#38bdf8` |
| copilot | `#db2777` (rose) | `#f472b6` |
| other | `#64748b` (slate) | `#94a3b8` |

Theming uses `[data-theme="light|dark|system"]` on `<html>`, persisted to `localStorage`.

Depth is expressed through borders only — no box shadows anywhere.

### Insight type → status colour mapping

| Insight type | Status token |
|---|---|
| anomaly | bad |
| efficiency | ok |
| recommendation | info |
| summary | neutral |

---

## Local Development

### Prerequisites

- .NET 10 SDK
- Node 22+
- PostgreSQL 16 (or Docker)

### Backend

```powershell
cd src/AiObservatory.Api
dotnet run
```

Set these environment variables (or user secrets):

```
DB_CONNECTION=Host=localhost;Database=aiobservatory;Username=...;Password=...
ANTHROPIC_API_KEY=sk-ant-...
OBSERVATORY_API_KEY=<any-guid>
```

Run EF Core migrations on first launch or after pulling new migrations:

```powershell
dotnet ef database update --project ../AiObservatory.Data --startup-project .
```

### Frontend

```powershell
cd src/AiObservatory.Web
npm install
npm run dev
```

Set `VITE_API_BASE_URL` in `.env.local` to point at the running API:

```
VITE_API_BASE_URL=http://localhost:5000
```

### Authentication (production)

The dashboard signs in with **Entra (Azure AD)** — no API key in the browser.
A single app registration (`fpaiobs-spa`) acts as both the SPA and the API it
calls; the App Service validates the bearer token. Machine callers (the
`observe-*` / `gemini-review` hooks) keep using the API key — an authenticated
human bypasses the key, a keyless machine request still needs one.

One-time setup (run as a tenant admin):

```powershell
az login
infra/scripts/setup-entra.ps1
```

It prints `VITE_AAD_CLIENT_ID` / `VITE_AAD_TENANT_ID` / `VITE_AAD_API_SCOPE` for
`src/AiObservatory.Web/.env.production`, and the `aadClientId` for the Bicep
param. Sign-in is restricted to the assigned user. Local `npm run dev` leaves
these unset, so it runs without sign-in against the local (keyless) API.

### Tests

```powershell
dotnet test                        # all .NET tests
cd src/AiObservatory.Web && npm test   # Vitest unit tests
npm run doctor                         # React Doctor diagnostics

docker run -d --name aiobs-test-pg -e POSTGRES_DB=aiobs_test -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:16 # spin up a local docker postgres if you want to unit test all locally
```

---

## CI / CD

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | PR + push to main | .NET build + test (PostgreSQL container); npm build |
| `deploy.yml` | CI success on main | Publishes API to `fpaiobs-api` App Service; deploys web to `fpaiobs-swa` SWA |
| `infra.yml` | Push to `infra/**` or manual | `az deployment group create` on `fpaiobs-rg` (westeurope) |
| `react-doctor.yml` | Every PR | React diagnostics with inline PR annotations; fails on error-severity findings |

Secrets required in the GitHub environment: `AZURE_CREDENTIALS`, `AZURE_STATIC_WEB_APPS_API_TOKEN`, `DB_CONNECTION`, `ANTHROPIC_API_KEY`, `OBSERVATORY_API_KEY`.

---

## Infrastructure

All Azure resources live in `fpaiobs-rg` (westeurope). Bicep templates in `infra/` are idempotent and redeploy via `infra.yml`.

| Resource | Name | Purpose |
|---|---|---|
| App Service Plan | `fpaiobs-plan` | F1 Free, Linux |
| App Service | `fpaiobs-api` | .NET 10 API |
| PostgreSQL Flexible | `fpaiobs-db` | Production database |
| Key Vault | `fpaiobs-kv` | Secrets (DB string, API keys) |
| Application Insights | `fpaiobs-ai` | Telemetry |
| Static Web App | `fpaiobs-swa` | React frontend → observatory.fixportal.org |

The API App Service uses a system-assigned managed identity to pull secrets from Key Vault at runtime — no secrets in app settings or environment variables.

> [!IMPORTANT]
> **Key Vault RBAC Role Assignment Drift (Runbook/Troubleshooting):**
> Both the API and Ingest App Services use system-assigned managed identities, and their Bicep templates define the Key Vault Secrets User role assignment deterministically using resource IDs. If either App Service is deleted and recreated, the next infrastructure deployment will fail with `RoleAssignmentUpdateNotPermitted` because Azure prevents updating the immutable `principalId` of an existing role assignment.
>
> **Resolution**: Before re-deploying, manually delete the stale role assignment for the App Service's identity from the Key Vault's Access Control (IAM) page in the Azure Portal.

---

## Background Intelligence Worker

`IntelligenceWorkerService` runs in-process on the API and periodically analyses recent aggregates to produce insights. It uses the Anthropic SDK (`claude-*`) via `AnthropicIntelligenceClient`, building prompts with `PromptBuilder` and parsing structured responses with `InsightResponseParser`.

Insight types generated:

- **summary** — period overview
- **efficiency** — cost-per-token observations
- **anomaly** — unusual spend spikes or drops
- **recommendation** — actionable optimisation suggestions

Insights are stored in the `Insights` table and surfaced in the `InsightsFeed` component.

---

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for
local setup and the PR checklist, and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
for community expectations. In short: branch from `main` as
`feat/<scope>` / `fix/<scope>` / `chore/<scope>`, PRs merge via **rebase** (no
merge commits, no squash), and CI must pass before merging.

To report a security vulnerability, follow [SECURITY.md](SECURITY.md) — please
do not open a public issue.

## License

[Apache-2.0](LICENSE) © 2026 Chris Dowling. See [NOTICE](NOTICE) for attribution.
