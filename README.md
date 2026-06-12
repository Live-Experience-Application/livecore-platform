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

### Reveal command

The Visibility module's reveal command makes a resource visible to the audience,
idempotently:

| Method | Route                                 | Authorized callers                             |
| ------ | ------------------------------------- | ---------------------------------------------- |
| `POST` | `/api/v1/sessions/{sessionId}/reveal` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

The session id in the path pins the workspace; the request body carries the
`organizationSlug` (resolved by the same token-claim-and-membership tenant check
as the other by-session-id routes) plus the target `resourceType`
(`Scene`/`ContentBlock`/`Entity`) and `resourceId`. The caller is authorized by
their role in the session's own workspace; a caller who cannot see the tenant, or
who is not a member of the session's workspace, is hidden as `404` (never `403`).

The command is **idempotent**: a required `Idempotency-Key` request header makes a
client retry safe. The first call applies the reveal (the resource's visibility
rule becomes `Visible`) and returns `Applied`; a repeat with the same key returns
`AlreadyApplied` and produces no duplicate effect (the System module's
`idempotency_keys` table records processed keys, and the visibility change is
itself idempotent).

By default the reveal is **audience-wide** (visible to the whole audience). An
optional `participantId` in the body makes it a **selected-participant** reveal —
the resource becomes visible only to that participant, and a non-selected
participant cannot see it. The target must be a participant of the session's own
workspace (otherwise the request is hidden as `404`). The durable
`ContentRevealed` event and its realtime delivery belong to the later realtime
event stream and are not emitted yet.

Whenever a reveal **actually changes** a resource's visibility, the command writes
an append-only audit record of the change (see "Audit log" below). A reveal that is
an idempotent retry, or that finds the resource already visible, changes nothing and
so writes no audit record.

### Audit log

The Audit module owns the tenant-scoped, append-only `audit_logs` table (the
documented critical index is `audit_logs(organization_id, created_at)`). It records
security-relevant actions as immutable facts; the first producer is the reveal
command (CORE-VIS-006), which appends a `VisibilityRuleChanged` entry capturing the
tenant, workspace, the authenticated **actor** (the caller's resolved user profile),
the governed resource, the optional selected-participant target and the before/after
visibility state. This satisfies the threat model's required control "audit creation
for visibility changes" (`docs/07_SECURITY_THREAT_MODEL.md`).

The audit log is **append-only**: there is no update or delete path, and only the
tenant boundary (`organization_id`) is a foreign key — the workspace, actor, resource
and participant references are recorded facts, so the trail survives later deletion of
the things it references and is never cascade-erased. The entry stores identifiers and
state names only, never revealed content (threat T7). No free-form scene/content body
is logged. The generic append-only audit log and its read API with the "View audit
log" authorization (Owner/Admin/Auditor) are the later `Audit, Export and Recap`
epic; there is no audit HTTP route yet.

### Participant visible feed (skeleton)

The Visibility module's first route returns a single participant's visible feed:

| Method | Route                                               | Authorized callers                                                |
| ------ | --------------------------------------------------- | ----------------------------------------------------------------- |
| `GET`  | `/api/v1/participants/{participantId}/visible-feed` | the participant's own user, or a `Host`/`CoHost` of its workspace |

The route path carries only the participant id, so the target organization is
supplied as a required `?organizationSlug=` query parameter (resolved by the same
token-claim-and-membership tenant check as the session by-id routes). Access is
granted only when the caller **owns** the participant (own feed) or is a `Host` or
`CoHost` of the participant's own workspace (preview). The feed is private, so
**every** denial — a cross-tenant or unknown participant, a removed participant, an
`Owner`/`Admin`/`Observer`/`Auditor` who is not the owner or a host, a different
participant, or a host of a different workspace — is hidden as `404` (never `403`),
and the participant-safe response never echoes any authorization rationale.

This is a **skeleton**: it establishes the route, its fail-closed object-level
authorization and a participant-safe response envelope whose item list is always
**empty**. The actual visible content (filtered reveal events / content blocks and
the server-side visibility-rule engine) belongs to the later Visibility, Reveal and
Realtime epics; broad external/anonymous participant feed delivery over the realtime
hub is likewise a Realtime-epic follow-up.

### Scene content APIs

The Scenes and Content modules expose their first HTTP routes for preparing a
workspace's scenes and the content blocks shown within them:

| Method | Route                                     | Authorized callers                             |
| ------ | ----------------------------------------- | ---------------------------------------------- |
| `GET`  | `/api/v1/workspaces/{workspaceId}/scenes` | any member of that workspace                   |
| `POST` | `/api/v1/workspaces/{workspaceId}/scenes` | workspace `Owner`, `Admin`, `Host` or `CoHost` |
| `POST` | `/api/v1/scenes/{sceneId}/content-blocks` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

The two workspace-scoped scene routes resolve the target organization from a
required `organizationSlug` (a query parameter on the `GET`, a body field on the
`POST`), exactly like the workspace by-id routes; the content-block route carries
only the scene id in its path, so it takes a required `?organizationSlug=` query
parameter like the session commands. Every route runs the same
token-claim-and-membership tenant check and then authorizes the caller by their
role in the relevant workspace (the scene's own workspace for the content-block
route). A caller who cannot see the tenant, or who is not a member of the
workspace, is hidden as `404` (never `403`); a known member who lacks the write
role is `403`.

Creating a scene assigns its ordering position server-side (appended after the
current last scene in the workspace); clients never supply or reorder positions.
Creating a content block stores it at its initial revision. Both creates return
`201 Created`.

The scene list projects by the caller's workspace role: host-capable and
metadata roles (`Owner`, `Admin`, `Host`, `CoHost`, `Auditor`) receive the full
scene metadata, while audience roles (`Participant`, `Observer`) receive a
stripped, audience-safe projection (scene id, title and order only — no internal
tenant/workspace ids, no host preparation timestamps, no authorization
rationale). Only the response shape differs by role; every member still receives
all of the workspace's scenes, since deciding which scenes an audience may
actually see is the later Visibility epic.

A content block's body is validated per type before it is stored: `Text` is
bounded plain text, `Media` a bounded reference string (the real asset linkage is
a later story), and `Data` a bounded, well-formed JSON document — each with its
own explicit size limit. An invalid or oversize body is rejected with `400`
before any persistence, and the rejected content is never echoed back.

### Realtime hub

The Realtime module exposes an authenticated [SignalR](https://learn.microsoft.com/aspnet/core/signalr/introduction)
hub for live sessions:

| Hub        | Path            | Authorized callers                          |
| ---------- | --------------- | ------------------------------------------- |
| SessionHub | `/hubs/session` | any authenticated caller (valid OIDC token) |

SignalR is part of the ASP.NET Core shared framework, so no new dependency is
added. The hub is `[Authorize]` and mapped with `RequireAuthorization()`, so its
`negotiate` and connection endpoints challenge an unauthenticated client with
`401` exactly like the REST endpoints. Because browser WebSocket clients cannot
set the `Authorization` header, the OIDC bearer token is also accepted from the
`access_token` query-string parameter — but **only** for hub paths (under
`/hubs`), never for the REST API, so a token is never read from a non-hub URL.
The token is fully validated by the same JWT bearer pipeline either way.

**Server-managed groups (CORE-RT-002).** On connect, a connection declares which
session it is joining through query-string identifiers —
`?organizationSlug=…&sessionId=…` and, for a participant, `&participantId=…` — and
the server resolves the caller's authorized relationship to that session and joins
the **server-computed** groups for it. Clients supply identifiers, never group
names (`docs/11_REALTIME_SYNC.md`: "Do not let clients choose arbitrary group
names"). The relationship maps to the minimal groups it needs:

| Relationship                                  | Groups joined                                             |
| --------------------------------------------- | --------------------------------------------------------- |
| Host-capable member (Owner/Admin/Host/CoHost) | `org:{org}`, `workspace:{ws}:hosts`, `session:{id}:hosts` |
| Observer member                               | `session:{id}:observers`                                  |
| Participant (owns an active record)           | `session:{id}:participant:{participantId}` only           |

A participant joins **only its own** participant group — a caller can never join a
participant group they do not own, so they can never subscribe to another
participant's feed. The whole connect is fail-closed: an unauthenticated or
unmappable principal, a denied tenant, an unknown session, a foreign/removed/
anonymous participant, or a workspace role with no defined realtime group (a
Participant/Auditor member without a participant record) all **abort** the
connection, indistinguishably. Anonymous-participant and auditor realtime channels
are deferred (the group taxonomy defines neither).

**Event append and delivery (CORE-RT-003).** The Realtime module owns the
session-scoped, append-only `session_events` table (the documented critical index
is `session_events(session_id, created_at, event_id)`) — the durable event stream
that reconnect replay later reconstructs from. When a command produces an event, the
Realtime publisher **persists** it to that stream and then **delivers** a
recipient-safe envelope over SignalR to the server-computed recipient groups
(`docs/11_REALTIME_SYNC.md`: "command → authorize → persist event → compute
recipients → … → send to recipient groups"; "Events are never broadcast blindly").

The first producer is the reveal command (`POST …/reveal`): when a reveal actually
changes visibility (the same change signal the audit uses, so a retry or no-op emits
nothing), it appends a `ContentRevealed` event. The delivered envelope carries resource
identifiers only, never resolved content, and excludes the internal addressing fields of
the stored event (the org/workspace ids, the actor and the routing target).

**Recipient-specific event projection (CORE-RT-004).** Delivery now computes recipients
**per recipient** so that "Realtime delivery never leaks hidden events"
(`docs/07_SECURITY_THREAT_MODEL.md` threat T3; `docs/11_REALTIME_SYNC.md`). Each event
records its **visibility subject** — the resource (kind + id) whose audience visibility
gates who may receive it (the event catalog's `visibilityProjection`, stored as new
nullable `session_events(visibility_subject_type, visibility_subject_id)` columns) — and
the Realtime recipient resolver turns it into a set of deliveries:

- The **session hosts** group always receives the event, with the **host** projection,
  which carries the routing target (the "to whom" confirmation hosts are entitled to).
- A **selected-participant** event reaches **only** that one participant's group (plus
  hosts), and only when they may see the subject; observers and other participants are
  never targeted (a non-selected participant is neither in that group nor passes the
  per-participant gate — the crown jewel).
- An **audience-wide** event is delivered to the **observers** group when the audience
  may see the subject, and is **fanned out to each active participant** of the session's
  workspace whose own per-participant visibility allows it (the connection model has no
  all-participants group, so the audience reaches participants only through their
  individual groups). The **audience** projection omits the routing target, so a
  participant never learns who else was targeted.

Every per-recipient and audience decision is delegated to the central Visibility engine
(`CanViewResource` / `CanParticipantViewResource`, reused — not duplicated), so the
realtime recipient set can never diverge from the REST visibility decision. An event with
no visibility subject (a later unconditional audience event such as `SessionStarted`) is
not gated.

Wiring the remaining catalog events (`SessionStarted`/`SessionEnded`), reconnect replay
and scale-out are later Realtime stories (`docs/11_REALTIME_SYNC.md`).

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
