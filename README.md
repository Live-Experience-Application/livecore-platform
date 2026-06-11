# livecore-platform

[![CI](https://github.com/Live-Experience-Application/livecore-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/Live-Experience-Application/livecore-platform/actions/workflows/ci.yml)

Generic Core Platform for live, role-aware, scene-based interactive sessions.

This repository must stay product-neutral. It must not contain ArcanOS, Pen-and-Paper, DnD, Enterprise or ScenarioOS domain language in source code.

## Owns

```text
API
Realtime hub
Domain model
Database migrations
Visibility engine
Reveal engine
Session event stream
Asset authorization
Audit log
Generic templates
TypeScript contracts
TypeScript SDK
Generic UI primitives and design tokens
```

## Does not own

```text
Campaigns
Dungeon Masters
Players
NPCs
Quests
Monster stats
Character sheets
Training scenarios
Facilitators
Trainees
Incidents
Debrief reports
```

Those belong to vertical repositories.

## Start here

Read in order:

1. `AGENTS.md`
2. `docs/00_START_HERE.md`
3. `docs/01_PRODUCT_VISION_AND_SCOPE.md`
4. `docs/02_ARCHITECTURE.md`
5. `docs/04_PRODUCT_BOUNDARIES.md`
6. `docs/07_SECURITY_THREAT_MODEL.md`
7. `csv/core_epics_stories.csv`

Do not implement code until the first story is selected.

## Repository layout

```text
LiveCore.slnx            .NET solution (apps + tests)
Directory.Build.props    repository-wide .NET build/lint enforcement
.editorconfig            formatting and C# code-style baseline
.gitattributes           line-ending normalization (LF in the repository)
.github/workflows/ci.yml CI pipeline (build, tests, format/lint, boundary scan, image builds)
.dockerignore            build-context exclusions for the container image builds
eslint.config.mjs        ESLint flat config for the TypeScript packages
.prettierrc.json         Prettier configuration (with .prettierignore)
apps/api                 ASP.NET Core API host (LiveCore.Api) - health endpoints, IdentityAccess module
apps/api/Dockerfile      container image for the API host (multi-stage)
apps/worker              Background worker host skeleton (LiveCore.Worker)
apps/worker/Dockerfile   container image for the worker host (multi-stage)
packages/contracts       @livecore/contracts  - TypeScript contract types (skeleton)
packages/sdk-ts          @livecore/sdk-ts     - TypeScript SDK client (skeleton)
packages/ui-core         @livecore/ui-core    - generic UI primitives (skeleton)
packages/design-tokens   @livecore/design-tokens - design tokens/theme contracts (skeleton)
tests/LiveCore.Api.UnitTests  xUnit unit tests for the API domain modules (IdentityAccess)
tests/LiveCore.SmokeTests  xUnit smoke and health endpoint tests for the hosts
scripts/boundary-scan.ps1  forbidden-term boundary scan for Core source
docs/                    architecture and product documentation
csv/                     backlog stories and forbidden term list
```

## Mobile-related Core extension

The Core includes product-neutral Entitlements, Quotas, Purchase Verification and Ad Eligibility contracts so that mobile apps cannot bypass limits or premium state client-side.
Core does not render ads, own mobile screens, or contain App Store / Google Play marketing copy.

## Prerequisites

- .NET SDK 10.0 or later
- Node.js 22 or later
- pnpm 10 (pinned via the `packageManager` field; with Corepack run `corepack enable pnpm` once, or prefix pnpm commands with `corepack`)
- Docker (optional; only needed to build and run the container images)

## Build, format, lint, test and boundary scan

Run all commands from the repository root. CI (`.github/workflows/ci.yml`)
calls these commands verbatim, so a green local run means a green pipeline.

### .NET solution (API, worker, smoke tests)

Build:

```bash
dotnet build LiveCore.slnx
```

Run the tests (unit and smoke):

```bash
dotnet test LiveCore.slnx
```

Verify formatting and code style (no files are changed; non-zero exit code on
violations):

```bash
dotnet format LiveCore.slnx --verify-no-changes
```

Apply formatting and code-style fixes:

```bash
dotnet format LiveCore.slnx
```

C# style rules live in `.editorconfig`. `Directory.Build.props` additionally
enforces them at build time (`EnforceCodeStyleInBuild`) and treats warnings as
errors, so `dotnet build` doubles as the .NET lint gate.

### TypeScript packages

Install dependencies:

```bash
pnpm install
```

Build all packages:

```bash
pnpm --recursive run build
```

Lint (ESLint; zero warnings allowed):

```bash
pnpm run lint
```

Verify formatting (Prettier; non-zero exit code on violations):

```bash
pnpm run format:check
```

Apply formatting:

```bash
pnpm run format
```

Run package test scripts (packages define none yet; this exits 0 and picks up
`test` scripts automatically as packages add them):

```bash
pnpm --recursive run test
```

### Boundary scan

Run the boundary scan (fails with a non-zero exit code if any forbidden
vertical term from `csv/forbidden_core_terms.csv` appears in Core source under
`apps/`, `packages/`, `tests/`, `scripts/` or `.github/`):

```powershell
# Windows (Windows PowerShell 5.1 or pwsh)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/boundary-scan.ps1
```

```bash
# Linux/macOS (PowerShell 7+)
pwsh -NoProfile -File scripts/boundary-scan.ps1
```

## Run the hosts locally

Start the API host (listens on `http://localhost:5062` by default, see
`apps/api/Properties/launchSettings.json`):

```bash
dotnet run --project apps/api
```

Start the background worker host (registers no jobs yet):

```bash
dotnet run --project apps/worker
```

### Health endpoints

The API host exposes two unauthenticated health endpoints:

| Endpoint        | Purpose                                                                                                                                    |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------------ |
| `/health/live`  | Liveness: the process is up and serving HTTP. Runs no dependency checks on purpose.                                                        |
| `/health/ready` | Readiness: runs the health checks tagged `ready` (currently the `database` check, registered only when a connection string is configured). |

Both return `200 OK` with the minimal JSON body `{"status":"Healthy"}`;
readiness returns `503` with `{"status":"Unhealthy"}` once a registered
readiness check fails. Because the endpoints are reachable without
authentication, the response carries only the overall status: no version
numbers, configuration values, host names or individual check details (see
`docs/07_SECURITY_THREAT_MODEL.md`).

### Structured logging

Both hosts write structured, single-line JSON log entries to stdout using the
JSON console formatter built into `Microsoft.Extensions.Logging` (UTC
timestamps, scopes included); no external logging dependency is used. Log
levels are configured per host in `appsettings.json`. Logs must carry
identifiers and metadata, never sensitive content (threat T7 in
`docs/07_SECURITY_THREAT_MODEL.md`).

### Identity (OIDC principal model)

Authentication is OIDC-first (`docs/adr/0005-oidc-first-authentication.md`):
the platform consumes tokens issued by an external OIDC provider (Keycloak by
default) and implements no custom password authentication.

CORE-ID-001 adds the principal model of the IdentityAccess module
(`apps/api/IdentityAccess/`): `OidcPrincipalMapper` normalizes the claims of a
validated token into an immutable `OidcPrincipal` (issuer + subject identity,
user vs. service account, optional display metadata, and the raw organization
claim values that later feed the tenant boundary). Mapping is fail-closed:
missing, conflicting or malformed security-relevant claims produce a typed
error, never a partially trusted principal, and organization claim matching is
exact and case-sensitive (threat T5 in `docs/07_SECURITY_THREAT_MODEL.md`).

The JWT bearer middleware that validates provider tokens at the edge landed
later with the first HTTP endpoints (see "Tenant model and HTTP API" below);
the `/api/v1/me` endpoint is still a follow-up.

### Persistence (user profile reference)

CORE-ID-002 adds the first persisted aggregate: the user profile reference
(`UserProfile`, keyed by OIDC issuer + subject), stored in PostgreSQL through
EF Core (`apps/api/Persistence/`, provider `Npgsql.EntityFrameworkCore.PostgreSQL`).
The connection string is read exclusively from configuration
(`ConnectionStrings:Database`, e.g. the environment variable
`ConnectionStrings__Database`); no credentials live in this repository. When
no connection string is configured the host starts without persistence and
without the `database` readiness check, so local runs and the test suite need
no database server (tests use the EF Core SQLite provider in-memory).

Migrations live in `apps/api/Persistence/Migrations` and are managed with the
pinned `dotnet-ef` local tool:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project apps/api
```

### Tenant model and HTTP API

The Identity and Tenant Boundaries epic builds the tenant model in the
IdentityAccess and Organizations modules: `Organization` (the tenant root),
`OrganizationMember` (a subject's generic role in an organization, drawn from
`docs/06_AUTHORIZATION_MATRIX.md` — the roles are not a linear hierarchy, so
they are matched exactly), and the `TenantContextResolver`, which turns an
authenticated principal plus a target organization into a trusted
`TenantContext` only when both the token's organization claim and a persisted
membership agree (defence in depth for tenant isolation, threat T5).

The Workspaces module adds the tenant-scoped `Workspace` aggregate,
`WorkspaceMember` (workspace-level roles) and `WorkspaceInvitation` (a member
invite carrying a single-use, scoped token — see below).

HTTP endpoints live under `/api/v1` (JSON, RFC 7807 Problem Details, the status
codes in `docs/08_API_CONTRACTS.md`). Requests are authenticated with an OIDC
JWT bearer token validated against the configured provider; configure it with:

```text
Authentication__Oidc__Authority = https://<your-oidc-issuer>
Authentication__Oidc__Audience  = <your-api-audience>
```

No identity-provider settings are committed to the repository. When no
`Authority` is configured the host still starts, but every authenticated
endpoint fails closed with `401` (never anonymous access); the unauthenticated
health endpoints stay reachable. Authorization is enforced server-side on every
request: the target organization is resolved from the request and verified
against the caller's membership, and cross-tenant or non-member access is hidden
as `404` rather than `403`.

The workspace routes implemented so far:

| Method | Route                                      | Authorized callers                                                  |
| ------ | ------------------------------------------ | ------------------------------------------------------------------- |
| `GET`  | `/api/v1/workspaces`                       | any workspace member (results filtered to the caller's memberships) |
| `POST` | `/api/v1/workspaces`                       | organization `Owner` or `Admin`                                     |
| `GET`  | `/api/v1/workspaces/{workspaceId}`         | members of that workspace                                           |
| `PUT`  | `/api/v1/workspaces/{workspaceId}`         | organization `Owner` or `Admin` (rename)                            |
| `POST` | `/api/v1/workspaces/{workspaceId}/members` | organization `Owner` or `Admin` (create invite)                     |

### Workspace member invites (scoped tokens)

`POST /api/v1/workspaces/{workspaceId}/members` creates a workspace invitation
with a single-use, scoped token. The token is generated with a cryptographically
secure RNG and is returned **once** in the creation response; only its SHA-256
hash is stored, and the token is never logged or returned again. Each token is
bound to one organization, one workspace, one role and an expiry, and is
single-use. It is a one-time join grant, not an authentication credential and
not a JWT (`docs/adr/0005-oidc-first-authentication.md`). Invite acceptance,
delivery and revocation endpoints are follow-up stories.

### Session lifecycle commands

Two by-session-id commands drive the session lifecycle state machine
(`Prepared` → `Live` → `Ended`):

| Method | Route                                | Authorized callers                             |
| ------ | ------------------------------------ | ---------------------------------------------- |
| `POST` | `/api/v1/sessions/{sessionId}/start` | workspace `Owner`, `Admin`, `Host` or `CoHost` |
| `POST` | `/api/v1/sessions/{sessionId}/end`   | workspace `Owner`, `Admin`, `Host` or `CoHost` |

The route path carries only the session id, so the target organization is
supplied as a required `?organizationSlug=` query parameter (resolved by the
same token-claim-and-membership tenant check as the workspace by-id routes).
The caller is then authorized by their role in the session's own workspace; a
caller who cannot see the tenant, or who is not a member of the session's
workspace, is hidden as `404` (never `403`). `start` requires the session to be
`Prepared` and `end` requires it to be `Live`; any other current state is a
`409 Conflict` that leaves the session unchanged.

These commands persist the session status transition (the authoritative state).
The durable `SessionStarted` / `SessionEnded` events and their realtime delivery
belong to the later realtime event stream and are not emitted yet.

## Container images

Both hosts ship a multi-stage Dockerfile (SDK build stage, runtime-only final
stage). Build from the repository root so the repository-wide build
configuration (`Directory.Build.props`, `.editorconfig`) applies inside the
image build; `.dockerignore` keeps the build context small.

Build the images:

```bash
docker build -f apps/api/Dockerfile -t livecore-api .
docker build -f apps/worker/Dockerfile -t livecore-worker .
```

Run the API container (Kestrel listens on container port 8080) and probe it:

```bash
docker run --rm -d -p 8080:8080 --name livecore-api livecore-api
curl http://localhost:8080/health/live
docker stop livecore-api
```

Run the worker container (no ports; it registers no jobs yet and idles):

```bash
docker run --rm livecore-worker
```

Image baseline:

- Both runtime images run as the non-root user built into the official .NET
  images (`USER $APP_UID`, a numeric UID so policies like `runAsNonRoot` can
  verify it).
- The runtime images contain only the published output: no SDK, no package
  caches, no build tooling.
- Only the API image exposes a port (8080, unprivileged); the worker serves
  no HTTP traffic.
- The images define no `HEALTHCHECK` instruction on purpose: the .NET runtime
  images ship no HTTP client tooling, and none is installed just for probing.
  Orchestration platforms (Compose, Kubernetes, load balancers) probe
  `GET /health/live` (liveness) and `GET /health/ready` (readiness) over HTTP
  instead; the worker's liveness is the process itself.
- Configuration is supplied at runtime through environment variables
  (for example `ASPNETCORE_ENVIRONMENT` and logging levels); no secrets are
  baked into the images.

Local development orchestration (Compose with database, auth and storage
services) lives in `livecore-deploy`, not in this repository (see
`docs/13_SELF_HOSTING_REQUIREMENTS.md`).

## Continuous integration

GitHub Actions runs `.github/workflows/ci.yml` on every push to `main` and on
every pull request. All jobs run on `ubuntu-latest` and execute the commands
documented above verbatim:

| Job               | What it runs                                                                                     |
| ----------------- | ------------------------------------------------------------------------------------------------ |
| `dotnet`          | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` on `LiveCore.slnx`            |
| `typescript`      | `pnpm install --frozen-lockfile`, `lint`, `format:check`, recursive `build` and `test`           |
| `boundary-scan`   | `pwsh -NoProfile -File scripts/boundary-scan.ps1` (forbidden vertical terms fail the build)      |
| `powershell-lint` | PSScriptAnalyzer (Error/Warning severity) over `scripts/*.ps1`                                   |
| `docker`          | `docker build` for both Dockerfiles, then container smoke tests (`/health/live`, worker startup) |

Line endings are normalized to LF in the repository via `.gitattributes`, so
the boundary scan and `dotnet format` behave identically on Linux CI and on
Windows working copies.

## License

This project is licensed under the GNU Affero General Public License v3.0 or later.

Commercial dual licensing may be offered in the future for organizations that require proprietary use, embedding, hosting, or distribution without AGPL obligations.

For commercial licensing inquiries, contact: singh.harwinder@outlook.copm
