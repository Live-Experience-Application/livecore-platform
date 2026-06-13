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
apps/api/Migrations.Dockerfile  one-shot migrations runner image (applies EF Core migrations before API rollout)
apps/worker              Background worker host (LiveCore.Worker) - runs the asset cleanup job
apps/worker/Dockerfile   container image for the worker host (multi-stage)
packages/contracts       @livecore/contracts  - TypeScript contract types (DTOs, enums, events)
packages/sdk-ts          @livecore/sdk-ts     - TypeScript SDK client (typed Core API client over @livecore/contracts)
packages/ui-core         @livecore/ui-core    - generic, framework-agnostic UI primitive contracts (variant vocabularies, prop shapes, variant defaults)
packages/design-tokens   @livecore/design-tokens - generic design tokens and theme contracts
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

Run package test scripts (the `@livecore/contracts`, `@livecore/sdk-ts`,
`@livecore/design-tokens` and `@livecore/ui-core` packages define type and
package-build tests; packages without a `test` script are skipped):

```bash
pnpm --recursive run test
```

### TypeScript contract package

`@livecore/contracts` (`packages/contracts`) is the stable, product-neutral
TypeScript mirror of the Core API surface that vertical apps consume
(CORE-SDK-001). It exports the request/response DTOs for the implemented
`/api/v1` routes, the generic enumerations (roles, lifecycle statuses, resource
and content kinds, quota/store/ad-eligibility codes) as both string-literal
unions and runtime `as const` tuples, the RFC 7807 `ProblemDetails` error shape,
the transport constants (`API_BASE_PATH`, request header names) and the realtime
session event vocabulary. Every type matches the API's JSON exactly (camelCase
fields, enums as stable string names); the package carries no vertical domain
language. The typed SDK client that calls these contracts is a separate package
(`@livecore/sdk-ts`, CORE-SDK-002).

The package is types-first and adds no runtime dependencies. Its `test` script
builds the package, type-checks the compile-time type assertions
(`tsconfig.test.json`) and runs package-build tests against the compiled output
with the Node built-in test runner.

### TypeScript SDK package

`@livecore/sdk-ts` (`packages/sdk-ts`) is the typed client a vertical app uses to
call the Core API (CORE-SDK-002). It wraps the implemented `/api/v1` routes with
methods that return the exact `@livecore/contracts` response types, grouped into
resource clients that mirror the Core server modules
(`client.workspaces`, `client.sessions`, `client.scenes`, `client.content`,
`client.visibility`, `client.realtime`, `client.assets`, `client.entitlements`,
`client.store`). Its only dependency is `@livecore/contracts`; transport uses the
global `fetch` (Node 22+, browsers), so it adds no runtime dependency, and a
`fetch` implementation can be injected for testing or a custom transport.

The SDK is OIDC-first and product-neutral: the caller supplies an access-token
provider (the SDK never holds a password and never mints a token), and every
method carries only generic Core vocabulary. Authorization stays server-side —
the SDK is a typed transport, not a security boundary
(`docs/07_SECURITY_THREAT_MODEL.md`). It fails closed when no token is available
(no request is sent), reuses a caller-supplied `Idempotency-Key` for the reveal
command (never a fresh key per retry), and turns a non-success response into a
typed `LiveCoreApiError` carrying the HTTP status and the RFC 7807 Problem
Details — never the access token or request body, so a `404` hidden resource or a
`403` denial surfaces as an error rather than a value. Its `test` script builds
the package, type-checks the compile-time type assertions (`tsconfig.test.json`)
and runs package-build tests against an injected transport with the Node built-in
test runner.

### TypeScript design tokens package

`@livecore/design-tokens` (`packages/design-tokens`) is the generic,
product-neutral design-token contract a vertical app themes the Core UI with
(CORE-SDK-003). Core owns the **contract** — the token categories (`color`,
`spacing`, `typography`, `radius`, `shadow`, `breakpoint`, `motion`) and the
stable generic keys within each (the semantic color roles, the t-shirt scales,
the motion steps), exported as `as const` tuples alongside their string-literal
unions exactly like the `@livecore/contracts` enums — plus a neutral `baseTheme`
default that satisfies the contract. A vertical owns the **values**: it re-skins
the Core UI by defining its own `Theme` (typically by spreading `baseTheme.tokens`
and overriding only what it wants), and the `defineTheme` helper makes the
compiler check that no required token is dropped. Themes are a vertical extension
mechanism (`docs/04_PRODUCT_BOUNDARIES.md`), so the package carries only generic
UI vocabulary and no vertical visual identity (`AGENTS.md`).

The package is types-first and adds no runtime dependencies; its stable runtime
surface is the scale-key tuples (for enumeration/validation), the `baseTheme`
value and the `defineTheme` authoring helper. Its `test` script builds the
package, type-checks the compile-time type assertions (`tsconfig.test.json`) and
runs package-build tests against the compiled output with the Node built-in test
runner.

### TypeScript UI core package

`@livecore/ui-core` (`packages/ui-core`) is the generic, product-neutral UI
primitive **contract** a vertical app builds its components on (CORE-SDK-004).
Core owns the **contract**: the variant vocabularies a primitive's props are
drawn from (the semantic `tone`, the `size` and `emphasis` scales, the surface
level and the layout options), exported as `as const` tuples alongside their
string-literal unions exactly like the `@livecore/contracts` enums and the
`@livecore/design-tokens` scales; the typed prop shape of each generic primitive
(`Surface`, `Stack`, `Text`, `Heading`, `Button`, `Badge`, `Field`, `Spinner`,
`Divider`, `Avatar`); and the pure `resolveVariant` helper plus its
`DEFAULT_VARIANT`, which fill a partially-specified variant with Core's stable
defaults so every vertical resolves a primitive's tone, size and emphasis
identically. A vertical owns the **rendering**: it implements the actual
components (typically React, but the contract is framework-agnostic) that accept
these props and apply their theme (`@livecore/design-tokens`). Vertical-specific
screens and UI wrappers are a vertical extension mechanism
(`docs/04_PRODUCT_BOUNDARIES.md`); Core defines no screen and carries only
generic UI vocabulary and no vertical domain language (`AGENTS.md`).

The package is types-first and adds no runtime dependencies; its stable runtime
surface is the variant tuples (for enumeration/validation), the default
constants and the `resolveVariant` helper, with the prop contracts being
compile-time types. Its `test` script builds the package, type-checks the
compile-time type assertions (`tsconfig.test.json`) and runs package-build tests
against the compiled output with the Node built-in test runner.

### Package versioning and changelog

So vertical apps can consume **stable, typed** Core packages with predictable
upgrade semantics, the four published packages (`@livecore/contracts`,
`@livecore/sdk-ts`, `@livecore/design-tokens`, `@livecore/ui-core`) follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) and are released
**together** (lockstep), so they always share one version (CORE-SDK-005). The
full process — what makes a major/minor/patch change, how a release is cut and
how it is enforced — is documented in `docs/23_PACKAGE_VERSIONING.md`.

Each package exports its release as a stable runtime value next to its
`PACKAGE_NAME`, so a consumer can introspect exactly which Core release it is
running against:

```ts
import { PACKAGE_NAME, VERSION } from "@livecore/contracts";
```

Every notable change is recorded in [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
format: each package keeps its own `CHANGELOG.md` (shipped with the package via
its `files` list) and the repository root keeps a workspace-level `CHANGELOG.md`
summarizing the release across all four packages. The agreement between the
`VERSION` constant, the package manifest version and the changelog's top entry is
not left to convention — each package's type tests check `VERSION` is a
well-formed, non-widened SemVer literal, and its package-build tests check
`VERSION` equals `package.json` and that `CHANGELOG.md` documents it — so a
release with the version, manifest or changelog out of step fails CI rather than
shipping.

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

Start the background worker host (runs the asset cleanup job when a database is
configured; see "Asset cleanup job" below):

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
later with the first HTTP endpoints (see "Tenant model and HTTP API" below), and
the `/api/v1/me` current-principal endpoint is now implemented (see "Current
principal" below).

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

### Applying migrations (deployment step)

The API host **never** applies migrations implicitly on startup (an implicit
startup migration is unsafe for a multi-instance deployment, where replicas would
race to migrate). The schema is applied by a separate, run-to-completion
**migrations runner** that must finish before the API rolls out (CORE-OPS-001).

`apps/api/Migrations.Dockerfile` builds the runner image: a self-applying EF Core
migrations bundle that applies every pending migration to the database named by
`ConnectionStrings__Database` and then exits (idempotent; no credentials are
baked in). Build and run it:

```bash
docker build -f apps/api/Migrations.Dockerfile -t livecore-migrations .
docker run --rm \
  -e ConnectionStrings__Database="Host=<db-host>;Port=5432;Database=<db>;Username=<user>;Password=<password>" \
  livecore-migrations
```

The same path runs without Docker via `dotnet ef database update --project apps/api`
against a configured `ConnectionStrings__Database`. The exact command, how to gate
an API rollout on it (Kubernetes Job / init container, a Compose `migrate` service,
a Railway pre-deploy command) and the standalone-bundle alternative are documented
in `docs/13_SELF_HOSTING_REQUIREMENTS.md`. CI's `migrations` job applies all
migrations to an empty PostgreSQL database on every change.

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

### Current principal

The IdentityAccess module exposes the current-principal endpoint (CORE-API-002):

| Method | Route        | Authorized callers                             |
| ------ | ------------ | ---------------------------------------------- |
| `GET`  | `/api/v1/me` | any authenticated **user** (their own context) |

`GET /api/v1/me` returns the authenticated caller's principal context: their own
user profile (the surrogate profile id, the OIDC identity pair and the optional
display metadata) plus the organization memberships they hold and the generic
role in each. The caller's profile is resolved (and provisioned on first sight)
through the same `UserProfileReferenceService` the other current-user routes use,
and the memberships are read with their roles in a single query
(`IOrganizationRepository.ListMembershipsByMemberAsync`).

It is fail-closed and tenant-isolated:

- an anonymous caller is challenged with `401` (the route group requires
  authorization);
- `/me` is a user concept, so a service-account principal is denied `403` (only a
  human user holds a profile and organization memberships) — the same rule the
  sibling `/me/quota-status` and `/me/ad-eligibility` routes apply;
- the membership list is the **intersection** of the caller's persisted
  memberships and the token's organization claims — the same token-asserted
  boundary the `TenantContextResolver` and the `GET /api/v1/organizations` listing
  enforce — so a persisted membership the token does not assert (a foreign tenant
  from the token's point of view) is never exposed (threat T5).

The response is a safe DTO of identifiers and the caller's own display metadata
only: it carries no access token, no raw organization-claim payload and no
authorization rationale (threat T7).

### Organization create and read

The Organizations module exposes the tenant create/read API (CORE-API-001):

| Method   | Route                                                         | Authorized callers                                             |
| -------- | ------------------------------------------------------------- | -------------------------------------------------------------- |
| `GET`    | `/api/v1/organizations`                                       | any authenticated user (only the organizations they belong to) |
| `POST`   | `/api/v1/organizations`                                       | any authenticated user (becomes the new tenant's `Owner`)      |
| `DELETE` | `/api/v1/organizations/{organizationSlug}/members/{memberId}` | organization `Owner` or `Admin` (remove member — see below)    |

The create and list routes are user-tenant operations, so a service-account principal is denied
`403` (only a human user holds an organization membership). The tenant boundary
is the token's organization claim, matched exactly — the same token-asserted
boundary the `TenantContextResolver` enforces:

- `GET` lists the organizations the caller is a member of, intersected with the
  organizations the token claims, so a persisted membership the token does not
  assert (a foreign tenant from the token's point of view) is never listed.
- `POST` creates a new tenant **only** for a slug the token claims; a slug the
  token does not claim is a foreign tenant, hidden as `404` (the create never
  reveals whether it exists). On success it makes the caller the founding
  `Owner` — the `Organization` row and its `OrganizationMember` are written
  **atomically**, so a tenant is never left ownerless. A slug that is already
  taken is `409 Conflict` and grants **no** membership, so a create can never
  escalate into ownership of a pre-existing organization (threats T5/T1). The
  organization is the tenant root, so the create body names the new tenant
  directly (slug + display name) and carries no parent organization.

The workspace routes implemented so far:

| Method   | Route                                                 | Authorized callers                                                  |
| -------- | ----------------------------------------------------- | ------------------------------------------------------------------- |
| `GET`    | `/api/v1/workspaces`                                  | any workspace member (results filtered to the caller's memberships) |
| `POST`   | `/api/v1/workspaces`                                  | organization `Owner` or `Admin`                                     |
| `GET`    | `/api/v1/workspaces/{workspaceId}`                    | members of that workspace                                           |
| `PUT`    | `/api/v1/workspaces/{workspaceId}`                    | organization `Owner` or `Admin` (rename)                            |
| `POST`   | `/api/v1/workspaces/{workspaceId}/archive`            | organization `Owner` (archive — see below)                          |
| `POST`   | `/api/v1/workspaces/{workspaceId}/members`            | organization `Owner` or `Admin` (create invite)                     |
| `DELETE` | `/api/v1/workspaces/{workspaceId}/members/{memberId}` | organization `Owner` or `Admin` (remove member — see below)         |

### Workspace member invites (scoped tokens)

`POST /api/v1/workspaces/{workspaceId}/members` creates a workspace invitation
with a single-use, scoped token. The token is generated with a cryptographically
secure RNG and is returned **once** in the creation response; only its SHA-256
hash is stored, and the token is never logged or returned again. Each token is
bound to one organization, one workspace, one role and an expiry, and is
single-use. It is a one-time join grant, not an authentication credential and
not a JWT (`docs/adr/0005-oidc-first-authentication.md`). Invite acceptance,
delivery and revocation endpoints are follow-up stories.

### Member removal (revoking access)

An authorized admin can remove a member, revoking their access (CORE-LIFE-001):

| Method   | Route                                                         | Authorized callers              |
| -------- | ------------------------------------------------------------- | ------------------------------- |
| `DELETE` | `/api/v1/workspaces/{workspaceId}/members/{memberId}`         | organization `Owner` or `Admin` |
| `DELETE` | `/api/v1/organizations/{organizationSlug}/members/{memberId}` | organization `Owner` or `Admin` |

Both routes hard-delete the addressed membership and return `204 No Content`. The
membership row **is** the access grant — every tenant- and workspace-scoped
authorization check reads it — so deleting it **revokes the subject's access on
their very next request**, fail-closed: a removed workspace member's by-id
workspace read becomes `404`, and a removed organization member can no longer
resolve the tenant at all (the `TenantContextResolver` requires a persisted
membership). The workspace route resolves its tenant from a required
`?organizationSlug=` query parameter (like the other workspace by-id routes); the
organization route carries the tenant's slug in its path.

Authorization mirrors the member-invite route: the **"Manage members"** matrix
row, **organization `Owner` or `Admin`** (`docs/06_AUTHORIZATION_MATRIX.md`),
matched exactly (`MembershipRole` is non-linear). Every step is fail-closed and
hidden as `404` for a caller who cannot see the tenant, a workspace/organization
not in the resolved tenant, or a `memberId` that belongs to another
workspace/tenant — so a member outside the caller's scope can never be removed or
probed for (threats T1/T5). A known tenant member who lacks `Owner`/`Admin` is
`403`.

The **last-Owner invariant** is guarded: removing the **sole** `Owner` of a
workspace or organization is rejected with `409 Conflict` and changes nothing — a
tenant must never be left ownerless (an ownerless organization would be
permanently unreachable). Removing an `Owner` when another `Owner` remains
succeeds.

Every successful removal appends an append-only `MemberRemoved` audit record (see
"Audit log" below) capturing the tenant, the workspace (for a workspace member),
the authenticated **actor** (the admin who removed the member), the removed
membership and the revoked role — never any token or content (threats T1/T6/T7).
The audit record is a recorded fact, so it survives the now-deleted membership it
references.

### Workspace archive (lifecycle end-state)

An owner can archive a workspace so it becomes read-only and drops out of active
lists (CORE-LIFE-009):

| Method | Route                                      | Authorized callers   |
| ------ | ------------------------------------------ | -------------------- |
| `POST` | `/api/v1/workspaces/{workspaceId}/archive` | organization `Owner` |

A workspace previously had create/read/update but **no lifecycle end-state**. The
decision recorded for this story is a **soft archive** — a `status` on the
`Workspace` aggregate (`Active` → `Archived`), not a hard delete — because a
workspace owns child sessions, scenes, content, entities, assets and an
append-only audit trail whose history must survive. Archiving flips the status
through a status transition like `Participant.Remove`/`Session.Start` and
preserves every child row. Archive is **clearly terminal**: there is no
un-archive command (a re-activate, if ever needed, is a separate, explicitly
scoped story), and archiving an already-archived workspace is a `409 Conflict`
that changes nothing and writes no duplicate audit fact.

The route carries only the `{workspaceId}`, so the tenant comes from a required
`?organizationSlug=` query parameter (resolved by the same
token-claim-and-membership tenant check as the other workspace by-id routes).
Authorization is **Owner-only** — the **"Delete workspace"** matrix row, whose
fail-closed Core default is `Owner` (`docs/06_AUTHORIZATION_MATRIX.md` grants
`Admin` only an _optional_ slot, so Core denies it) — matched exactly
(`MembershipRole` is non-linear). Every step is fail-closed and hidden as `404`
for a caller who cannot see the tenant or names an unknown/cross-tenant
workspace; a known tenant member who is not an `Owner` (including an `Admin`) is
`403`.

Once archived, the workspace is **read-only**: its authoring mutations are
rejected with `409 Conflict` — rename (`PUT /workspaces/{id}`), member invite
(`POST .../members`), session create (`POST .../sessions`) and scene create
(`POST .../scenes`) — while reads still succeed and **member removal stays
available** (revoking access is a safety operation, not authoring). The archived
workspace is **excluded from the active list** (`GET /api/v1/workspaces` filters
to `Active`) but remains reachable through the by-id read, which now carries the
lifecycle `status`. Extending the read-only guard to deeper child-resource
mutations (content blocks, entities, reveals/hides, asset links) is a follow-up;
those are governed transitively today (no new sessions or scenes can be created
to host new content) and the authoritative archived state lives on the aggregate.

Every successful archive appends an append-only `WorkspaceArchived` audit record
(see "Audit log" below) capturing the tenant, the workspace, the authenticated
**actor** (the owner who archived it) and the `Active → Archived` status
transition — never any content (threats T1/T5/T7). Unlike a deletion, the archive
records the before/after status because the workspace survives.

### Session create and list

The Sessions module exposes the workspace-scoped create and list API
(CORE-API-003), so a session is reachable over HTTP before its start/end
lifecycle commands operate on it:

| Method | Route                                       | Authorized callers                             |
| ------ | ------------------------------------------- | ---------------------------------------------- |
| `GET`  | `/api/v1/workspaces/{workspaceId}/sessions` | any member of that workspace                   |
| `POST` | `/api/v1/workspaces/{workspaceId}/sessions` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

Both routes carry the `{workspaceId}` in the path and resolve the target
organization from a required `organizationSlug` (a query parameter on the `GET`,
a body field on the `POST`), run the same token-claim-and-membership tenant check
as the other workspace-scoped routes, and then authorize the caller by their role
in that workspace. A caller who cannot see the tenant, or who is not a member of
the workspace, is hidden as `404` (never `403`); a known member who lacks the
create role is `403`.

`POST` creates a new `Prepared` session (the lifecycle status is assigned
server-side; a client can never create a session that is already live or ended)
and returns `201 Created` with the generic session DTO. The workspace's
`session.active.max` quota is enforced on create through the existing quota
services: the create **checks** the quota (a workspace already running its
maximum number of concurrent live sessions cannot create another and is rejected
with `409 Conflict`) but does **not** consume it — the active-session count stays
owned by `start` (which consumes a slot) and `end` (which releases it), so a
created `Prepared` session never double-counts against the live ceiling. When no
quota governs the deployment the create proceeds unchanged.

`GET` lists the workspace's sessions (filtered to that tenant and workspace, so a
member only ever sees their own workspace's sessions). Unlike the scene list there
is no host-vs-participant projection split: a session is a single generic resource
with no hidden content, so every workspace member receives the same safe DTO
(identifiers, the display title, the lifecycle status and the server timestamps).

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

These commands persist the session status transition (the authoritative state)
and, once it is persisted, emit the matching durable session event and audit fact
(CORE-EVT-001). `start` publishes a `SessionStarted` event and appends a
`SessionStarted` audit record (the `Prepared → Live` transition); `end` publishes a
`SessionEnded` event and appends a `SessionEnded` audit record (the `Live → Ended`
transition). The event is published through `ISessionEventPublisher` by the
**endpoint** (matching the reveal command), so the Realtime module stays the sole
owner of delivery. Both are **subjectless audience events** (no visibility subject,
no selected participant), so the recipient resolver — reused, not duplicated —
delivers each to the whole session audience (the hosts, the observers and every
active participant), and reconnect replay re-delivers them. Because the emit happens
only after a successful, guarded transition, each `start`/`end` persists **exactly
one** event and one audit fact, while a `409` out-of-state command emits neither.

### Session cancel (lifecycle off-ramp)

A host can cancel a not-yet-started session so it never runs (CORE-LIFE-010):

| Method | Route                                 | Authorized callers                             |
| ------ | ------------------------------------- | ---------------------------------------------- |
| `POST` | `/api/v1/sessions/{sessionId}/cancel` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

Sessions could previously be created, started and ended but had **no cancel/delete**.
The decision recorded for this story is a **soft cancel** — a new `Cancelled` value on
the `Session` aggregate's lifecycle status (`Prepared` → `Cancelled`), **not a hard
delete** — because a session is the foreign-key anchor of the **append-only**
`session_events` stream and the `audit_logs` trail, whose history must **never** be
deleted or cascade-erased. Cancelling flips the status through a guarded transition
like `Session.Start`/`Session.End` (and like the workspace archive, CORE-LIFE-009) and
preserves every append-only row. It is a new value in the existing `status` string
column (persisted by name), so it needs **no schema migration**.

Cancel is valid **only from `Prepared`** — a not-yet-started session: a live session
must be **ended**, not cancelled, and the terminal states are final, so cancelling a
session that is `Live`, `Ended` or already `Cancelled` is a `409 Conflict` that changes
nothing and writes no audit fact. A cancelled session never opened a live timeline, so
its `startedAt`/`endedAt` stay null, and it can be neither started, ended nor cancelled
again. Because a `Prepared` session has consumed no `session.active.max` quota (that is
owned by `start`/`end`), cancelling one releases nothing.

The route, tenant resolution and authorization are **identical to the start/end
commands** — the same required `?organizationSlug=`, the same session-control roles
(`Owner`/`Admin`/`Host`/`CoHost`, matched exactly because `MembershipRole` is
non-linear), and the same fail-closed, hidden-`404` mapping (a caller who cannot see
the tenant or is not a member of the session's workspace is `404`, never `403`; a known
workspace member who lacks the role is `403`). The endpoint reuses the **same** shared
lifecycle pipeline as start/end rather than duplicating it.

Every successful cancel appends an append-only `SessionCancelled` audit record (see
"Audit log" below) capturing the tenant, the workspace, the authenticated **actor** (the
host who cancelled it), the cancelled session and the `Prepared → Cancelled` status
transition — never any content (threats T1/T5/T7). Like the workspace archive, the
cancel records the before/after status because the session survives the transition.

### Participant presence events (join / leave)

A participant joining or leaving a session now appends and delivers the documented
`ParticipantJoined` / `ParticipantLeft` session events (CORE-EVT-002). These catalog
events (`docs/09_EVENT_CATALOG.md`) existed but were never emitted — the join flow
(`SessionParticipantJoinService`) returned an admission only. This story wires the
emission on the two participant-presence transitions:

- **Join** — `SessionParticipantJoinService.JoinAsync` emits a `ParticipantJoined` event
  on (and only on) an **admission**. The pure decision is unchanged (a session/participant
  outside the caller's tenant/workspace, a removed participant or a non-joinable session is
  a fail-closed denial); a denial emits nothing.
- **Leave** — the symmetric `SessionParticipantLeaveService.LeaveAsync` removes a
  participant from a session's audience over the participant aggregate's soft-delete
  (`Participant.Remove`) and emits a `ParticipantLeft` event on (and only on) an **actual
  departure**. Removing an already-removed participant is an idempotent no-op that emits
  nothing, so each real departure appends **exactly one** event and a repeat appends none.

Both events are emitted through the **reused** `ISessionEventPublisher` + recipient
resolver (the Realtime module stays the sole owner of delivery — these flows do not
duplicate the anti-leak routing). Like the CORE-EVT-001 `SessionStarted`/`SessionEnded`
events they are **subjectless audience events** (no visibility subject, no selected
participant), so the resolver delivers each to the whole session audience: the hosts
(**always — host-visible**, `docs/06_AUTHORIZATION_MATRIX.md`), the observers and every
active participant (the configurable audience of `docs/09_EVENT_CATALOG.md`). Because the
leave performs `Participant.Remove` **before** publishing, the just-departed participant is
no longer in the active-participant fan-out, so a leaver never receives their own removal
(the optional participant feed).

The payloads are **identifier-only**: each carries the participant's surrogate **id** and
nothing else — never the display name or any other participant PII (threat T7). The
participant id is an opaque surrogate (not a name, not a user identity), so the audience
learns only **which** participant (by id) joined or left. `ParticipantJoined` records the
joining participant's linked **user** as its actor (or none, for an anonymous participant);
`ParticipantLeft` is **System**-emitted (no actor). No audit record is written (these are
realtime presence events, not security-audited state transitions), and no schema migration
is needed — the `session_events` table persists the new event-type names in its existing
`event_type` string column. The join/leave HTTP endpoints and the persisted participant
connection metadata remain later stories.

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

### Hide (un-reveal) command

The Visibility module's hide command is the **inverse** of reveal (CORE-REV-001, the
"Reveal Lifecycle" hide / un-reveal): a host can take a reveal back so a previously
visible resource becomes `Hidden` again and the audience (or the selected
participant) stops seeing it.

| Method | Route                               | Authorized callers                             |
| ------ | ----------------------------------- | ---------------------------------------------- |
| `POST` | `/api/v1/sessions/{sessionId}/hide` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

The route, request body (`organizationSlug`, `resourceType`, `resourceId`, optional
`participantId`), tenant resolution and authorization are identical to the reveal
command — the same fail-closed `404`/`403`/`400` mapping and the same reveal roles,
hidden as `404` for a caller who cannot see the tenant or is not a member of the
session's workspace. The endpoint reuses the **same** `RevealService` (and so the
same `idempotency_keys` store and audit producer), so hide is not a parallel
duplicate of reveal — the two are one idempotent command with opposite target
states acting in the same dimension (an audience-wide hide flips the audience-wide
rule; a selected-participant hide flips only that participant's rule, leaving the
audience and other participants untouched).

The command is **idempotent**: a required `Idempotency-Key` request header makes a
client retry safe. The first call applies the hide (the resource's visibility rule
becomes `Hidden`) and returns `Applied`; a repeat with the same key returns
`AlreadyApplied` and produces no duplicate effect. The hide uses its **own**
per-tenant idempotency scope (distinct from reveal), so a client may reuse the same
key value for a matching reveal/hide pair without one short-circuiting the other.
Because an absent rule already means hidden, hiding a resource that has no visible
rule (or whose rule is already hidden) is a **no-op**: it changes nothing, writes no
audit record and emits no event.

When — and only when — a hide **actually changes** a resource's visibility, the
command appends an append-only `VisibilityRuleChanged` audit record of the
`Visible → Hidden` transition (see "Audit log" below) and emits a durable
`ContentHidden` session event. Unlike a reveal, the hide event carries **no
visibility subject**: the resource is now hidden, so a subject-gated projection
would (correctly, for a reveal) exclude the very recipients who must be told to
remove it. Instead the event is routed by its coarse target — a selected-participant
hide reaches only that participant (plus hosts), an audience-wide hide reaches the
observers and every active participant — carrying resource **identifiers only**,
never resolved content.

### Scene and content lifecycle session events

Activating a scene and changing a resource's visibility now surface as the documented
session events so a reconnecting client can reconstruct state (CORE-EVT-003). Two catalog
events (`docs/09_EVENT_CATALOG.md`) existed but were never emitted; this story wires them onto
the **existing** reveal/hide commands — on (and only on) a real visibility change, the same
change signal the audit record and the `ContentRevealed`/`ContentHidden` events already use, so
a retry or a no-op emits nothing:

- **`SceneActivated`** — emitted when a reveal makes a `Scene` visible. There is no separate
  active-scene command, so revealing a scene to the audience **is** the documented "scene
  switch". The payload carries the scene **id** only.
- **`VisibilityRuleChanged`** — emitted on every reveal **and** hide that actually changes a
  rule, carrying the resource identifiers and the new visibility **state** name. This is the
  realtime session event, **distinct from** the append-only `VisibilityRuleChanged` audit record
  the same command writes (one is live-state delivery, the other a security record).

Each new event **concerns a governed resource**, so it carries that resource as its **visibility
subject** and is delivered through the **reused** `ISessionEventPublisher` + `SessionEventRecipientResolver`
projection (CORE-RT-004) — the Realtime module stays the sole owner of delivery; this flow adds
no parallel routing. The resolver **gates** each event through the central Visibility engine: the
session hosts always receive it (host-content roles see everything) and the audience receives it
only when they may see the resource. So an audience-wide `SceneActivated` reaches the hosts and
every recipient who may see the scene (the "authorized session audience"), a selected scene reveal
reaches only that one participant (plus hosts), and a `VisibilityRuleChanged` for a **hide** —
whose subject is now hidden — reaches the **hosts only**, the security-relevant host-facing case.
A participant for whom a resource is hidden **never** receives an event about it: there is **no
leakage of hidden resources** (threats T2/T3), and reconnect replay re-applies the same gate.
The payloads are server-composed **identifiers and state names only**, never resolved content
(threat T7).

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
is logged.

CORE-AUD-001 (the `Audit, Export and Recap` epic) makes the log **generic**: a single
`AuditLogEntry.Create(...)` factory records **any** security-relevant `AuditAction` as
an append-only fact, with every part beyond the tenant and the action optional — an
organization-level **or** workspace-scoped action, a user **or** system actor, an
optional governed resource (the resource type and id are supplied as a pair or omitted
entirely), an optional selected-participant target and an **optional** before/after
state (a generic action such as a session start or a member invite is not a state
transition, so `new_state` is now nullable). `ForVisibilityRuleChange` is now a thin
specialization of that generic factory, so the reveal producer is unchanged and
visibility logic is not duplicated. The generic action catalog
(`VisibilityRuleChanged`, `SessionStarted`, `SessionEnded`, `MemberInvited`,
`MemberRemoved`, `EntityDeleted`, `ContentBlockDeleted`, `SceneDeleted`, `AssetDeleted`, `WorkspaceArchived`,
`SessionCancelled`) is
extensible without a schema change because the action persists by its stable name; each producer command wires its
own action in its own story. The member-removal command (CORE-LIFE-001) is the first wired producer of
`MemberRemoved`, appending an entry whenever an authorized admin removes a
workspace or organization member (the threat-model control for access revocation).
The entity-deletion command (CORE-LIFE-003) wires `EntityDeleted`, appending an entry
whenever an authorized host deletes an entity (the deletion's "authorized and audited"
control); the dependents it cascades are consequences of the one action and are not
separately audited. The content-block-deletion command (CORE-LIFE-004) wires
`ContentBlockDeleted` the same way — one append-only fact per content-block deletion, its cascaded
visibility rules and asset links being consequences of the one action — the consistent application of
`docs/adr/0012-resource-deletion-cascades-dependents.md` ("audit the deletion"). The scene-deletion command
(CORE-LIFE-005) wires `SceneDeleted` identically — one append-only fact per scene deletion, its cascaded
child content blocks, visibility rules and asset links and the remaining scenes' order re-pack being
consequences of the one action. The host-initiated asset-deletion command (CORE-LIFE-006) wires `AssetDeleted`
the same way — one append-only fact per asset deletion, its cascaded asset links and the removed storage object
being consequences of the one action; the storage object key is never recorded (only the asset id; threats
T4/T7). The workspace-archive command (CORE-LIFE-009) wires `WorkspaceArchived` — but unlike the deletion
producers it records a real STATE TRANSITION (the workspace survives), so the entry carries the before/after
status names (`Active` → `Archived`) like a visibility change, capturing the owner who archived the workspace
and the archived workspace itself. The session-cancel command (CORE-LIFE-010) wires `SessionCancelled` the same
way — another surviving STATE TRANSITION rather than a deletion, so the entry carries the before/after status
names (`Prepared` → `Cancelled`), capturing the host who cancelled the session and the cancelled session itself;
the session row (and its append-only `session_events`) survives, never deleted. The session start/end commands
(CORE-EVT-001) wire `SessionStarted` and `SessionEnded` the same surviving-transition way — the start endpoint
records the `Prepared` → `Live` transition and the end endpoint the `Live` → `Ended` transition, each capturing
the host who ran the command and the session itself — appended alongside the durable session event the same
command emits (the audit fact is the security record; the session event is the realtime delivery).

The audit log is still written only as a side effect of an **already-authorized**
command, so audit writes are inherently authorized. CORE-AUD-005 (the epic's final
story) adds the **audit query permissions** that make the audit **read** path generic
and authorized. `AuditQueryPolicy` is the reusable, fail-closed server-side decision of
who may read the append-only log — the "View audit log" row of
`docs/06_AUTHORIZATION_MATRIX.md`, whose secure default authorized set is exactly
**Owner/Admin/Auditor**. The audit role (`Auditor`) is allowed here — this is the one
place the matrix grants it a first-class `yes` (it is only `audit-only`, and denied, on
the content/asset policies). `Host` is the matrix's deployment-`optional` grant and is
**denied by Core's fail-closed default**; CoHost, the audience roles
(Participant/Observer) and any undefined role are denied (deny-by-default; threats
T1/T5). Because the audit read is a binary access grant rather than a host-vs-audience
split, there is a **single** safe read view (`AuditLogEntryView`, identifiers/enums/state
names only — never content, threat T7) handed to an authorized reader, and
`AuditQueryPolicy.Project` yields the empty set to any unauthorized role (fail-closed
defence in depth). The policy sits on top of the existing tenant-scoped read
(`IAuditLogRepository.ListByOrganizationAsync`, which filters by `organization_id` so one
tenant's records are never returned through another tenant's id — threat T5), so a future
audit query endpoint composes the trusted tenant resolution, this permission and the
projection exactly as the export/recap projectors are the reusable core their later
endpoints sit on. `csv/api_routes.csv` defines no audit route, so there is still no audit
HTTP route.

### Participant visible feed

The Visibility module's by-participant route returns a single participant's visible feed:

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

Once authorized, the feed returns the participant's **actually-visible** resources
(CORE-API-005), computed server-side by the participant-aware
`VisibilityPreviewService` (CORE-API-004), which decides every candidate resource
through the central `VisibilityPolicy` — **the same** primitive the realtime recipient
resolver uses, so the REST feed can never diverge from realtime delivery or
per-resource access (the visibility decision lives in exactly one place). A participant
sees a resource when an **audience-wide** visible rule, or a visible rule scoped to
**exactly them** (a selected-participant private reveal), applies; a resource revealed
only to a **different** participant is excluded — the selected-participant guarantee, so
a participant never sees another participant's private reveal. Each feed item carries
only the participant-safe resource **identity** (the resource kind name and id),
matching the realtime audience event payload (`SessionEventEnvelope.ForAudience`) — never
resolved content. The feed is empty only when the participant currently has nothing
visible.

Resolving each visible-resource identity into rendered, participant-safe content
(text/media/data), and broad external/anonymous participant feed delivery over the
realtime hub, remain Realtime-epic follow-ups.

### Scene content APIs

The Scenes and Content modules expose their first HTTP routes for preparing a
workspace's scenes and the content blocks shown within them:

| Method | Route                                     | Authorized callers                             |
| ------ | ----------------------------------------- | ---------------------------------------------- |
| `GET`  | `/api/v1/workspaces/{workspaceId}/scenes` | any member of that workspace                   |
| `POST` | `/api/v1/workspaces/{workspaceId}/scenes` | workspace `Owner`, `Admin`, `Host` or `CoHost` |
| `GET`  | `/api/v1/scenes/{sceneId}`                | any member of the scene's workspace            |
| `POST` | `/api/v1/scenes/{sceneId}/content-blocks` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

The two workspace-scoped scene routes resolve the target organization from a
required `organizationSlug` (a query parameter on the `GET`, a body field on the
`POST`), exactly like the workspace by-id routes; the by-scene-id read and the
content-block route carry only the scene id in their path, so they take a required
`?organizationSlug=` query parameter like the session commands. Every route runs
the same token-claim-and-membership tenant check and then authorizes the caller by
their role in the relevant workspace (the scene's own workspace for the
by-scene-id and content-block routes — discovered from the loaded scene row after
the tenant boundary is enforced). A caller who cannot see the tenant, or who is
not a member of the workspace, is hidden as `404` (never `403`); a known member
who lacks the write role is `403`.

Creating a scene assigns its ordering position server-side (appended after the
current last scene in the workspace); clients never supply or reorder positions.
Creating a content block stores it at its initial revision. Both creates return
`201 Created`.

The scene list **and the by-scene-id read** (`GET /api/v1/scenes/{sceneId}`,
CORE-API-007) project by the caller's workspace role through the same projector:
host-capable and metadata roles (`Owner`, `Admin`, `Host`, `CoHost`, `Auditor`)
receive the full scene metadata, while audience roles (`Participant`, `Observer`)
receive a stripped, audience-safe projection (scene id, title and order only — no
internal tenant/workspace ids, no host preparation timestamps, no authorization
rationale). Only the response shape differs by role; every member still receives
all of the workspace's scenes, since deciding which scenes an audience may
actually see is the later Visibility epic.

A content block's body is validated per type before it is stored: `Text` is
bounded plain text, `Media` a bounded reference string (the real asset linkage is
a later story), and `Data` a bounded, well-formed JSON document — each with its
own explicit size limit. An invalid or oversize body is rejected with `400`
before any persistence, and the rejected content is never echoed back.

### Entity relationship removal

The Entities module owns generic `EntityRelationship` edges — directed graph edges
between two entities. Until now an edge could be **added but never removed** (the
graph only grew); CORE-LIFE-002 (the "Resource Lifecycle and Deletion" epic) adds
the inverse so a host can remove one:

| Method   | Route                                                                    | Authorized callers                        |
| -------- | ------------------------------------------------------------------------ | ----------------------------------------- |
| `DELETE` | `/api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}` | workspace `Owner`/`Admin`/`Host`/`CoHost` |

The route pins the `{workspaceId}` in its path and resolves the target organization
from a required `?organizationSlug=` query parameter (the same token-claim-and-membership
tenant check as the other workspace by-id routes). The **parent workspace is resolved
first** and the edge is then loaded through the tenant- **and** workspace-scoped
repository lookup, because a relationship's endpoint foreign keys do not DB-enforce that
the edge and its endpoints share a workspace — so an edge that lives in another workspace,
or in a workspace owned by another tenant, is never reachable to remove even when its id
is known (threats T1/T5). On success the addressed edge is hard-deleted and the route
returns `204 No Content`; only that one edge row is removed, leaving both endpoint
entities intact.

It is fail-closed at every step and hidden as `404` for a caller who cannot see the
tenant, is not a member of the route's workspace, or names an edge that belongs to
another workspace/tenant — and **removing a non-existent edge is a safe `404`** (it
reveals nothing and changes nothing). A known workspace member who lacks the remove role
is `403`; entity relationships are host-prepared content, so the remove role set is the
host-capable `Owner`/`Admin`/`Host`/`CoHost` (the same set that creates scenes and content
blocks), matched exactly (`MembershipRole` is non-linear). Faithful to the add-edge
precedent (CORE-ENT-003), removal emits no event and writes no audit record.

### Entity deletion

A host can delete an entity, and its dependents are cleaned up consistently
(CORE-LIFE-003, the "Resource Lifecycle and Deletion" epic):

| Method   | Route                                                  | Authorized callers                        |
| -------- | ------------------------------------------------------ | ----------------------------------------- |
| `DELETE` | `/api/v1/workspaces/{workspaceId}/entities/{entityId}` | workspace `Owner`/`Admin`/`Host`/`CoHost` |

The route pins the `{workspaceId}` in its path and resolves the target organization from a required
`?organizationSlug=` query parameter (the same token-claim-and-membership tenant check as the
entity-relationship removal and other workspace by-id routes). The **parent workspace is resolved
first** and the entity is then loaded through the tenant- **and** workspace-scoped repository lookup,
so an entity that lives in another workspace, or in a workspace owned by another tenant, is never
reachable to delete even when its id is known (threats T1/T5).

**Cascade, not block** (`docs/adr/0012-resource-deletion-cascades-dependents.md`). Deleting an entity
removes it **together with** its dependents, atomically in one transaction, rather than refusing the
deletion while any dependent remains. The dependents come in two shapes and are handled by their nature:

- its directed **`EntityRelationship` edges** (both endpoints) — these hold real foreign keys to the
  entity (`ON DELETE CASCADE`), but the deletion removes them **explicitly** first so the cascade is
  deterministic and provider-independent (the database cascade then remains as defence in depth);
- its **`visibility_rules`** (the audience-wide rule and every selected-participant rule governing the
  entity) and its **`asset_links`** — these reference the entity **polymorphically** (`resource_id` /
  `target_id` are not foreign keys), so the database cannot cascade them and the application removes them
  explicitly. Leaving them behind would dangle: a stale visible rule a later resource could inherit (a
  visibility leak; threats T2/T5) or a link through which an asset could claim access via a target that
  no longer exists (threat T4). Only the link rows are removed — the linked **assets** are untouched, and
  the two endpoint entities of a removed edge are untouched.

The removals, the entity delete and the audit append run inside a **single database transaction**, so a
deletion is applied whole or not at all.

It is fail-closed at every step and hidden as `404` for a caller who cannot see the tenant, is not a
member of the route's workspace, or names an entity that belongs to another workspace/tenant — and
**deleting a non-existent entity is a safe `404`** (it reveals nothing and changes nothing). A known
workspace member who lacks the delete role is `403`; entities are host-prepared content, so the delete
role set is the host-capable `Owner`/`Admin`/`Host`/`CoHost` (the same set that creates scenes and
content blocks and removes entity relationships), matched exactly (`MembershipRole` is non-linear).

Every successful deletion appends an append-only `EntityDeleted` audit record (see "Audit log" below)
capturing the tenant, the workspace, the authenticated **actor** (the host who deleted) and the deleted
entity — never any content (threats T1/T5/T7). The audit record is a recorded fact, so it survives the
now-deleted entity it references. Faithful to the member-removal / edge-removal precedents, the deletion
emits no realtime session event (the event catalog defines none for entity deletion).

### Content block deletion

A host can delete a content block from a scene, and its dependents are cleaned up consistently
(CORE-LIFE-004, the "Resource Lifecycle and Deletion" epic):

| Method   | Route                                                      | Authorized callers                        |
| -------- | ---------------------------------------------------------- | ----------------------------------------- |
| `DELETE` | `/api/v1/scenes/{sceneId}/content-blocks/{contentBlockId}` | workspace `Owner`/`Admin`/`Host`/`CoHost` |

The route pins the `{sceneId}` in its path (pairing with the content-block create route
`POST /api/v1/scenes/{sceneId}/content-blocks`) and resolves the target organization from a required
`?organizationSlug=` query parameter (the same token-claim-and-membership tenant check as the create
route). The **parent scene is resolved first** (within the resolved tenant), the scene's own workspace is
discovered from the loaded row, the caller is authorized by their role in that workspace, and the content
block is then loaded through the tenant-, workspace- **and** scene-scoped repository lookup — so a content
block that lives in another scene, workspace or tenant is never reachable to delete even when its id is
known (threats T1/T5).

**Cascade, not block** (`docs/adr/0012-resource-deletion-cascades-dependents.md`), handled consistently
with the entity deletion (CORE-LIFE-003). Deleting a content block removes it **together with** its
dependents, atomically in one transaction:

- its **revisions** are not a separate table — a content block's revision history is the inline monotonic
  `revision_number` on the `content_blocks` row (`csv/database_tables.csv` lists no revisions table), so
  removing the row removes the block together with its revisions; no separate revision cleanup is needed;
- its **`visibility_rules`** (the audience-wide rule and every selected-participant rule governing the
  content block) and its **`asset_links`** reference the content block **polymorphically** (`resource_id` /
  `target_id` are not foreign keys), so the database cannot cascade them and the application removes them
  explicitly. Leaving them behind would dangle: a stale visible rule a later resource could inherit (a
  visibility leak; threats T2/T5) or a link through which an asset could claim access via a target that no
  longer exists (threat T4). Only the link rows are removed — the linked **assets** are untouched.

The removals, the content block delete and the audit append run inside a **single database transaction**, so
a deletion is applied whole or not at all.

It is fail-closed at every step and hidden as `404` for a caller who cannot see the tenant, is not a member
of the scene's workspace, or names a content block that belongs to another scene/workspace/tenant — and
**deleting a non-existent content block is a safe `404`** (it reveals nothing and changes nothing). A known
workspace member who lacks the delete role is `403`; content blocks are host-prepared content, so the delete
role set is the host-capable `Owner`/`Admin`/`Host`/`CoHost` (the same set that creates content blocks and
deletes entities), matched exactly (`MembershipRole` is non-linear).

Every successful deletion appends an append-only `ContentBlockDeleted` audit record (see "Audit log" above)
capturing the tenant, the workspace, the authenticated **actor** (the host who deleted) and the deleted
content block — never any content body (threats T1/T5/T7). The audit record is a recorded fact, so it
survives the now-deleted content block it references. Faithful to the entity-deletion precedent, the
deletion emits no realtime session event (the event catalog defines none for content block deletion).

### Scene deletion

A host can delete a scene; the remaining scenes re-pack their ordering and the scene's child content is
cleaned up consistently (CORE-LIFE-005, the "Resource Lifecycle and Deletion" epic):

| Method   | Route                                               | Authorized callers                        |
| -------- | --------------------------------------------------- | ----------------------------------------- |
| `DELETE` | `/api/v1/workspaces/{workspaceId}/scenes/{sceneId}` | workspace `Owner`/`Admin`/`Host`/`CoHost` |

The route pins the `{workspaceId}` in its path (pairing with the scene create route
`POST /api/v1/workspaces/{workspaceId}/scenes`) and resolves the target organization from a required
`?organizationSlug=` query parameter (the same token-claim-and-membership tenant check as the other
workspace by-id routes). The **parent workspace is resolved first**, the caller is authorized by their role
in that workspace, and the scene is then loaded through the tenant- **and** workspace-scoped repository
lookup — so a scene that lives in another workspace, or in a workspace owned by another tenant, is never
reachable to delete even when its id is known (threats T1/T5).

**Cascade, not block** (`docs/adr/0012-resource-deletion-cascades-dependents.md`), handled consistently
with the entity (CORE-LIFE-003) and content-block (CORE-LIFE-004) deletions. Deleting a scene removes it
**together with** its dependents, atomically in one transaction:

- its **child content blocks** reference the scene through a real foreign key (`scene_id`,
  `ON DELETE CASCADE`), so the database would itself cascade them; the deletion removes them **explicitly**
  first (the cascade stays deterministic and provider-independent) **and** removes each child block's own
  **polymorphic** `visibility_rules` and `asset_links` — the same per-content-block cleanup CORE-LIFE-004
  performs, which the database cannot cascade — so no dangling rule/link is ever left behind (threats
  T2/T4/T5). A block's revision history is inline on its row, so it goes with the row;
- the scene's **own `visibility_rules`** (the audience-wide rule and every selected-participant rule
  governing the scene) reference it **polymorphically** (`resource_id` is not a foreign key), so they are
  removed explicitly too. A scene is **not** an asset-link target (only content blocks and entities are), so
  the scene itself has no asset links to clean up. Only the link/rule rows are removed — the linked
  **assets** are untouched.

After the scene is gone, the **remaining scenes re-pack their ordering without gaps**: the survivors are
re-numbered to a contiguous `scene_order` (`0, 1, 2, …`) in their existing deterministic order, reusing the
SCENE-001 ordering logic (`Scene.Reorder` over the `(scene_order, id)` listing). Their relative order is
preserved; only the gap the deleted scene left is closed.

The removals, the scene delete, the order re-pack and the audit append run inside a **single database
transaction**, so a deletion is applied whole or not at all.

It is fail-closed at every step and hidden as `404` for a caller who cannot see the tenant, is not a member
of the route's workspace, or names a scene that belongs to another workspace/tenant — and **deleting a
non-existent scene is a safe `404`** (it reveals nothing and changes nothing). A known workspace member who
lacks the delete role is `403`; scenes are host-prepared content, so the delete role set is the host-capable
`Owner`/`Admin`/`Host`/`CoHost` (the same set that creates scenes and deletes entities and content blocks),
matched exactly (`MembershipRole` is non-linear).

Every successful deletion appends an append-only `SceneDeleted` audit record (see "Audit log" above)
capturing the tenant, the workspace, the authenticated **actor** (the host who deleted) and the deleted
scene — never any content (threats T1/T5/T7). The audit record is a recorded fact, so it survives the
now-deleted scene it references. Faithful to the entity- and content-block-deletion precedents, the deletion
emits no realtime session event (the event catalog defines none for scene deletion).

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
no visibility subject (an unconditional audience event such as `SessionStarted`/`SessionEnded`,
CORE-EVT-001) is not gated: the whole audience receives it.

**Reconnect replay with filtering (CORE-RT-005).** A client that reconnects rebuilds its
live state from the durable stream over a REST route, with the same per-recipient filter
applied again so "reconnect replay filters events again" (`docs/07_SECURITY_THREAT_MODEL.md`
threat T3; `docs/09_EVENT_CATALOG.md` "Reconnect replay"):

| Method | Route                                 | Authorized callers                                       |
| ------ | ------------------------------------- | -------------------------------------------------------- |
| `GET`  | `/api/v1/sessions/{sessionId}/events` | the session audience (host, observer or own-participant) |

The session id in the path pins the workspace; the target organization is the required
`?organizationSlug=` query parameter, and a participant replaying its own feed identifies
itself with `?participantId=` exactly like the hub connection. The caller's authorized
relationship to the session — and the **server-managed groups** it maps to — is resolved by
the **same** connection resolver the live hub uses (CORE-RT-002), so a host replays as a
host, an observer as an observer, and a participant **only its own** feed (a caller can
never replay another participant's feed). The replay then re-runs the **live** recipient
computation (CORE-RT-004) for each event after the acknowledged cursor and keeps only the
deliveries addressed to the caller's own groups, with the same host-vs-audience projection
live delivery uses — so a replayed item is the projection the recipient would have received
live, and a hidden event is never replayed. The optional `?afterEventId=` is the caller's
last acknowledged event id; events strictly after it are replayed (an unknown cursor
replays the whole stream, which the client deduplicates per `docs/11_REALTIME_SYNC.md`).
Like the participant-visible feed, the stream is private: every denial — a foreign tenant,
an unknown session, a caller with no legitimate relationship, or a `participantId` the
caller does not own — is hidden as `404` (never `403`).

**Scale-out abstraction (CORE-RT-006).** `docs/11_REALTIME_SYNC.md` ("Scale-out") calls for a
"Valkey/Redis-compatible backplane later when multiple API instances run". The Realtime module now defines
that seam: `IRealtimeBackplane` is the single transport boundary a server-computed event delivery crosses
on its way to the connected clients. The default `InProcessRealtimeBackplane` fans a delivery out to the
connections held by **this** API instance over the SignalR hub (`IHubContext<SessionHub>`, part of the
shared framework — no new dependency); a multi-instance deployment substitutes a Valkey/Redis-backed
implementation so the **same** delivery also reaches connections held by **other** instances. The real
backplane wiring (the Redis package and its configuration) lives with deployment, not in this repository
(`docs/13_SELF_HOSTING_REQUIREMENTS.md`).

The backplane receives an **already-authorized** delivery — one recipient-safe payload addressed to exactly
**one** server-managed group (`RealtimeGroups`), produced by the per-recipient recipient resolver
(CORE-RT-004) and only ever invoked by the publisher. It has no event, no visibility subject and no way to
enumerate recipients, so it **cannot** widen the audience: it only forwards what the resolver already
authorized. The per-recipient recipient computation therefore stays the **single send path**, and
"Realtime delivery never leaks hidden events" (threat T3 in `docs/07_SECURITY_THREAT_MODEL.md`) holds for
every backplane — in-process or scaled-out — by construction.

The `SessionStarted`/`SessionEnded` lifecycle events are wired over this delivery path by CORE-EVT-001 (see
"Session lifecycle commands"): the start/end endpoints publish them through `ISessionEventPublisher` as
**subjectless** audience events, so the recipient resolver delivers each to the whole session audience and
reconnect replay re-delivers them. Wiring the remaining catalog events over this delivery path is a later
Realtime story (`docs/11_REALTIME_SYNC.md`).

### Asset metadata

The Assets module owns generic asset metadata: the record of a stored file or
media object whose binary content lives in private, S3-compatible object storage,
never in PostgreSQL (`docs/12_STORAGE_ASSETS.md`; ADR 0006). CORE-AST-001 adds the
first piece — the `Asset` aggregate and its tenant- and workspace-scoped `assets`
table (`apps/api/Assets/`; the documented critical index is `assets(workspace_id,
id)`). The row is **metadata only**: the storage coordinates (`storage_provider`,
`bucket`, `object_key`), the `content_type`, the `created_by` creator, the
lifecycle `status` and — once an upload is confirmed — the `size_bytes` and
`checksum`.

Assets are **private by default** (`docs/07_SECURITY_THREAT_MODEL.md` threat T4
"Asset leak"). The metadata model carries no public URL, no "is public" flag and
no shareable token; the storage coordinates address a private bucket and are never
participant-facing and never written to logs (the log-safe `ToString` excludes the
provider, bucket, object key and checksum, so an asset can never be located from
logs — threats T4/T7). An asset is reachable only through an authorized,
short-lived signed URL after a server-side permission check, which is a later
story (the signed download flow, CORE-AST-004); there is no public or static URL
in any status.

An asset is workspace-scoped and tenant-scoped, so every lookup is scoped by
organization id then workspace id (the organization boundary is checked before the
workspace boundary), and one workspace's asset can never be read through another
workspace's or another tenant's id (threats T5/T1). The tenant and workspace
foreign keys cascade on delete; the optional `created_by` user foreign key **sets
null** on delete, so deleting the creating user anonymizes the asset record rather
than deleting an asset other content may link to (mirrors `participants.user_id`).
An asset is registered `Pending` when its upload intent is created (size and
checksum unknown) and moves to `Available` once the upload is confirmed, via a
guarded state transition that rejects confirming an already-available asset (so a
confirm can never silently overwrite a different recorded size/checksum).

CORE-AST-001 is the metadata aggregate, its persistence and its EF migration only.
The upload intent flow (`POST /api/v1/assets/upload-intent`, CORE-AST-003), the
signed download URL flow (`GET /api/v1/assets/{assetId}/download-url`,
CORE-AST-004), linking to content blocks/entities (CORE-AST-005) and the cleanup
job (CORE-AST-006) are later stories; there is no asset HTTP route yet.

### Asset storage adapter

CORE-AST-002 adds the S3-compatible storage adapter **port** — the single seam
between Core and the private object storage that holds an asset's binary content
(`docs/12_STORAGE_ASSETS.md`; ADR 0006). `IAssetStorage` mints **short-lived,
signed URLs** for the two object accesses Core ever needs: an `Upload` URL (for
the upload-intent flow, CORE-AST-003) and a `Download` URL (for the signed
download flow, CORE-AST-004). It signs only for an already-resolved, tenant- and
workspace-scoped `Asset`'s own storage coordinates — never an arbitrary bucket or
object key — so a signed URL can only ever address an object inside the caller's
tenant and workspace (threats T5/T1).

The security guarantees are enforced by the **type system** so no adapter can
forget them: the only value the port hands back is a `SignedAssetUrl`, which
cannot be constructed without an absolute URL and a strictly positive lifetime no
longer than `MaxLifetime` (one hour). A long-lived, non-expiring or public/static
URL is therefore unrepresentable — assets are private by default and reachable
only through a short-lived signed URL after a server-side permission check (the
epic acceptance criterion; threat T4 "Asset leak"). The signed URL is itself a
secret (it embeds the object key and signature), so `SignedAssetUrl.ToString()`
excludes the URL and logs only the operation and expiry (threats T4/T7).

The port does **not** authorize the caller — that is the consuming flow's job:
the upload-intent (CORE-AST-003) and signed-download (CORE-AST-004) endpoints
authorize server-side (role + tenant + workspace + visibility) and only then ask
the adapter to mint a URL. The adapter is a dumb, secure signer; minting is the
last step after the permission check has passed.

The **concrete, provider-specific adapter** (its SDK and the object-storage
endpoint/credentials) is supplied by the deployment, exactly as a Valkey/Redis
backplane replaces the in-process realtime default (CORE-RT-006); Core carries no
object-storage SDK dependency and no storage credentials in source
(`docs/13_SELF_HOSTING_REQUIREMENTS.md`; threat T7). Until one is wired, the
default registration is the **fail-closed** `UnconfiguredAssetStorage`: every
operation throws `AssetStorageNotConfiguredException` rather than serving bytes
some insecure way, so the private-by-default posture holds even when storage is
not configured (mirroring how the host runs without a database connection string
or OIDC authority and denies cleanly). There is no asset HTTP route yet.

### Asset upload intent

CORE-AST-003 adds the Assets module's first HTTP route — the upload-intent flow,
the "Create upload intent" step of the asset lifecycle (`docs/12_STORAGE_ASSETS.md`):

| Method | Route                          | Authorized callers                             |
| ------ | ------------------------------ | ---------------------------------------------- |
| `POST` | `/api/v1/assets/upload-intent` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

The route has no path parameters, so the request body carries the
`organizationSlug` (resolved to the tenant by the same token-claim-and-membership
check as the other routes) and the target `workspaceId`, plus the `contentType`
of the object the client intends to upload. The caller is authorized **server-side**
by their role in that workspace: a caller who cannot see the tenant, or who is not
a member of the target workspace, is hidden as `404` (never `403`), and a known
member who lacks the upload role is `403`. Only after authorization is the
`contentType` validated (`400` if missing or malformed).

On success the command registers a new **`Pending`** `Asset` and returns `201` with
the asset id, its status and a **short-lived, signed upload URL** (and its expiry)
the client uploads the object to. The storage coordinates are minted **server-side**:
the deployment's private provider and bucket (configured under `Assets:Storage:*`,
with safe private-by-default fallbacks — only the naming, never credentials) plus a
tenant- and workspace-scoped, collision-free object key. A client never supplies a
bucket or object key, so an upload can never be pointed at another tenant's or
workspace's object (threats T5/T1). The asset is **private by default**: the only
access handed out is the single short-lived signed URL after the permission check
(the epic acceptance criterion; threat T4 "Asset leak").

The signed URL is minted through the `IAssetStorage` adapter **before** the metadata
row is persisted, so when no object storage is configured the fail-closed
`UnconfiguredAssetStorage` makes the request `503` and **no** orphan pending asset is
left behind — the private-by-default posture holds even unconfigured, exactly as the
host denies cleanly without a database or OIDC authority. Linking to content
blocks/entities (CORE-AST-005) and the cleanup job (CORE-AST-006) are later stories.

### Asset signed download

CORE-AST-004 adds the Assets module's read flow — the "download URL requires
authorization" step of the asset lifecycle (`docs/12_STORAGE_ASSETS.md`):

| Method | Route                                   | Authorized callers                            |
| ------ | --------------------------------------- | --------------------------------------------- |
| `GET`  | `/api/v1/assets/{assetId}/download-url` | host-content viewers of the asset's workspace |

The route path carries only the asset id, so the target organization is supplied as a
required `?organizationSlug=` query parameter (resolved by the same
token-claim-and-membership tenant check as the by-scene-id content-block route). The
asset is then loaded **within** that resolved tenant, its own workspace is **discovered
from the loaded row** after the tenant boundary is enforced, and the caller is authorized
by their role in the asset's own workspace. A caller who cannot see the tenant, an unknown
or cross-tenant asset, and a caller who is not a member of the asset's workspace are all
hidden as `404` (never `403`); a known member who is not an authorized viewer is `403`.

The authorized viewers are the host-content roles (`Owner`, `Admin`, `Host`, `CoHost` —
the "View host-only content" capability of `docs/06_AUTHORIZATION_MATRIX.md`), reused
through the central Visibility module's role classification so visibility logic is not
duplicated. Audience roles (`Participant`, `Observer`) and the audit role are **denied
fail-closed**: an asset becomes audience-visible only once it is linked to a visible
content block or entity (CORE-AST-005), which does not exist yet, so until then only
host-content roles may download (threat T4 "Asset leak"; threat T2 visibility leak).

On success the endpoint mints a **short-lived, signed download URL** (and its expiry)
through the `IAssetStorage` adapter and returns `200 OK`; the asset stays **private** —
the only access handed out is that single signed URL, minted **only after** the
permission check passes (the epic acceptance criterion; threat T4). The asset must be
`Available`: a still-`Pending` asset (its upload not yet confirmed) is `409 Conflict`,
reported only to an authorized viewer. When no object storage is configured the
fail-closed `UnconfiguredAssetStorage` makes the request `503` and no URL is produced, so
the private-by-default posture holds even unconfigured.

The download authorizer is the central Assets `AssetDownloadPolicy` (CORE-AST-005, below):
host-content roles may always download, an audience role may download only when the asset
is linked to a **visible** content block/entity, and every other role is denied
fail-closed. The cleanup job (CORE-AST-006) is a later story.

### Asset linking

CORE-AST-005 adds the Assets module's linking flow — the "asset can be linked to a
ContentBlock or Entity" step of the asset lifecycle (`docs/12_STORAGE_ASSETS.md`):

| Method | Route                            | Authorized callers                             |
| ------ | -------------------------------- | ---------------------------------------------- |
| `POST` | `/api/v1/assets/{assetId}/links` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

The route path carries only the asset id, so the request body carries the `organizationSlug`
(resolved by the same token-claim-and-membership tenant check as the reveal command), the
generic `targetType` (`ContentBlock` or `Entity` — never a `Scene`) and the `targetId`. The
asset is loaded **within** the resolved tenant, its own workspace is **discovered from the
loaded row**, and the caller is authorized **server-side** by their role in the asset's own
workspace: a caller who cannot see the tenant, an unknown or cross-tenant asset, and a
non-member of the asset's workspace are hidden as `404` (never `403`); a known member who
lacks the link role is `403`.

The link's `target_id` is a **polymorphic** reference (not a database foreign key), so the
create flow enforces the **same-workspace coupling**: it resolves the target content block /
entity through the workspace-scoped repository of the asset's **own** organization and
workspace **before** creating the link (mirrors `visibility_rules.resource_id`,
`content_blocks.scene_id`, `entities.entity_type_id`). A target not in the asset's workspace —
including one in another workspace or tenant — is hidden as `404` and no link is created, so
an asset can never be linked to a foreign-workspace or foreign-tenant resource (threats
T5/T1). A repeat of the same link is `409` (the per-workspace unique
`asset_links(workspace_id, asset_id, target_type, target_id)` key prevents duplicates); a new
link returns `201`.

The `asset_links` table (the documented critical index is `asset_links(workspace_id,
asset_id)`) is the **join** that lets an asset **inherit** the audience visibility of the
resource it is attached to. Linking **never** makes an asset public — it only records the
attachment whose audience visibility the **central Visibility engine** governs. The signed
download flow (CORE-AST-004) now consults these links through `AssetDownloadPolicy`, which
**reuses** `VisibilityPolicy.CanViewResource` (visibility logic is not duplicated): an
**audience** role (`Participant`/`Observer`) may download an asset **only** when it is linked
to a content block or entity **visible to the audience**; host-content roles
(`Owner`/`Admin`/`Host`/`CoHost`) may always download; the audit role and any undefined role
are **denied fail-closed**. The asset stays **private by default** and is reached only through
the single short-lived signed URL minted after the permission check (the epic acceptance
criterion; threat T4 "Asset leak"; threat T2 visibility leak). Per-participant asset access
(an asset linked to a resource revealed only to one participant) is a later story.

### Asset cleanup job

CORE-AST-006 adds the Assets module's lifecycle cleanup — the final step of the asset
lifecycle (`docs/12_STORAGE_ASSETS.md`). It is a periodic background sweep that runs in the
**worker** host (`apps/worker`; `docs/02_ARCHITECTURE.md`: the worker owns "cleanup" and async
jobs), behind no HTTP route. It reclaims **abandoned upload intents**: an asset registered
`Pending` when its upload intent was created (CORE-AST-003) whose upload was never confirmed
(CORE-AST-004) within the deployment's grace window (`Assets:Cleanup:PendingRetention`, default
24 hours). Each leaves a stale metadata row and possibly an orphaned object in private storage;
the sweep deletes the **object first**, then the **metadata row**, so a row never outlives its
object and no orphaned object is ever left behind. The asset's links cascade away with the row.

Object deletion is a new **server-side** `IAssetStorage` operation: the deployment-supplied
adapter deletes the object directly with its own credentials — no signed URL is produced and no
bytes are served — so cleanup only ever **removes** access and can never weaken the
private-by-default posture (threat T4 "Asset leak"). It is **fail-closed** like the signing
operations: with no configured storage adapter (`UnconfiguredAssetStorage`) the delete throws and
the sweep removes **nothing** — it never deletes a metadata row whose object it could not delete.
Only `Pending` assets are ever touched; a confirmed (`Available`) asset — real, possibly-linked
content — is **never** reclaimed, however old (defence in depth: the candidate query is
pending-only and the sweep re-checks each asset's status).

The cleanup logic lives in the Assets module (`ExpiredPendingAssetCleanupService`); the worker
only schedules it (`AssetCleanupBackgroundService`, every `Assets:Cleanup:SweepInterval`, in
bounded `Assets:Cleanup:BatchSize` batches), and like the API host it is **gated on a configured
database connection string** (no database -> the worker starts but runs no cleanup loop). No
storage credentials live in Core; the concrete S3-compatible adapter is supplied by the
deployment (`docs/13_SELF_HOSTING_REQUIREMENTS.md`; ADR 0006; threat T7). Because the worker now
reuses the Core domain assembly, its runtime image uses the ASP.NET base (see
`apps/worker/Dockerfile`); it still serves no HTTP traffic and exposes no port.

### Asset deletion

A host can delete an asset; its links are removed and the underlying storage object is deleted
(CORE-LIFE-006, the "Resource Lifecycle and Deletion" epic):

| Method   | Route                      | Authorized callers                        |
| -------- | -------------------------- | ----------------------------------------- |
| `DELETE` | `/api/v1/assets/{assetId}` | workspace `Owner`/`Admin`/`Host`/`CoHost` |

Until now an asset could be created (CORE-AST-003), linked (CORE-AST-005) and downloaded (CORE-AST-004)
but never removed by a host — only the background cleanup job (CORE-AST-006) could reclaim still-`Pending`,
never-confirmed upload intents, so an `Available` asset could not be deleted at all. This adds the
host-initiated path. The route path carries only the `{assetId}`, so the target organization is a required
`?organizationSlug=` query parameter (the same token-claim-and-membership tenant check as the signed download
route). The asset is loaded **within** the resolved tenant (`FindByIdInOrganizationAsync`, the predicate leads
with `organization_id`), its own workspace is **discovered from the loaded row** after the tenant boundary is
enforced, and the caller is authorized by their role in the asset's own workspace — so an asset in another
workspace or tenant is never reachable to delete even when its id is known (threats T1/T5).

**Cascade, not block** (`docs/adr/0012-resource-deletion-cascades-dependents.md`), handled consistently with
the entity (CORE-LIFE-003), content-block (CORE-LIFE-004) and scene (CORE-LIFE-005) deletions. In **one
transaction** the deletion:

- removes the asset's **`asset_links`** — `asset_links.asset_id` is a real `ON DELETE CASCADE` foreign key, so
  the database would itself cascade them when the row is removed; the application removes them **explicitly
  first** so the cascade is deterministic, observable and provider-independent, and the story's "its links are
  removed" is a directly testable effect (the database cascade then remains as defence in depth). Only the link
  rows are removed — the linked content blocks/entities are untouched. An asset is **not** a visibility resource
  and is never an asset-link **target**, so there are no `visibility_rules` or target-side links to clean up;
- then **deletes the underlying storage object** via the `IAssetStorage` adapter
  (`IAssetStorage.DeleteObjectAsync`, the same server-side delete the cleanup job uses — no signed URL, no bytes
  served, so it only ever **removes** access; threat T4 "Asset leak");
- then **deletes the metadata row** and appends an append-only `AssetDeleted` audit record.

The storage object is deleted **before** the metadata row — the same ordering the upload intent uses (mint the
signed URL before persisting the row) — so a row is never removed while its object remains: a deletion never
leaves an orphaned object behind, and **a storage failure leaves no dangling row**. When no object storage is
configured the fail-closed `UnconfiguredAssetStorage` throws when the object delete is attempted, the whole
transaction rolls back having removed nothing, and the request is **`503`** (the private-by-default posture
holds even unconfigured, exactly as the upload-intent flow fails closed).

It is fail-closed at every step and hidden as `404` for a caller who cannot see the tenant, is not a member of
the asset's workspace, or names an asset that belongs to another workspace/tenant — and **deleting a
non-existent asset is a safe `404`** (it reveals nothing and changes nothing). A known workspace member who
lacks the delete role is `403`; assets are host-prepared content, so the delete role set is the host-capable
`Owner`/`Admin`/`Host`/`CoHost` (the same set that creates upload intents and links assets and deletes
scenes/entities/content blocks), matched exactly (`MembershipRole` is non-linear). On success the route returns
`204 No Content`.

Every successful deletion appends an append-only `AssetDeleted` audit record (see "Audit log" above) capturing
the tenant, the workspace, the authenticated **actor** (the host who deleted) and the deleted asset — never any
storage coordinate (only the asset id; threats T4/T7). The audit record is a recorded fact, so it survives the
now-deleted asset it references. Faithful to the entity-, content-block- and scene-deletion precedents, the
deletion emits no realtime session event (the event catalog defines none for asset deletion).

### Asset-link removal

A host can unlink an asset from a content block or entity; the asset and the target are unaffected
(CORE-LIFE-007, the "Resource Lifecycle and Deletion" epic):

| Method   | Route                                     | Authorized callers                        |
| -------- | ----------------------------------------- | ----------------------------------------- |
| `DELETE` | `/api/v1/assets/{assetId}/links/{linkId}` | workspace `Owner`/`Admin`/`Host`/`CoHost` |

An `AssetLink` was created (`POST /api/v1/assets/{assetId}/links`, CORE-AST-005) but, until now, could only be
removed as a **cascade** when its asset, target or workspace was deleted (CORE-LIFE-003/004/005/006) — there was
no way to detach a single link. This adds the **inverse** of the create-link route, reusing the existing
`AssetLinkRepository`. The route path carries the `{assetId}` (pairing with the link create route), so the target
organization is a required `?organizationSlug=` query parameter (the same token-claim-and-membership tenant check
as the signed download and asset-delete routes). The asset is loaded **within** the resolved tenant
(`FindByIdInOrganizationAsync`, the predicate leads with `organization_id`), its own workspace is **discovered
from the loaded row** after the tenant boundary is enforced, and the caller is authorized by their role in the
asset's own workspace.

The link is then resolved through the tenant- **and** workspace-scoped `FindByIdAsync` of the asset's **own**
organization and workspace and must attach **exactly the addressed asset** (`AssetLink.LinksAsset`) — so a link
that lives in another workspace or tenant, or one whose id resolves in the workspace but attaches a **different**
asset, is never reachable to remove even when its id is known (threats T1/T5). On success the addressed link is
hard-deleted and the route returns `204 No Content`; **only that one link row is removed** — the linked **asset**
and the target **content block / entity** are both left intact (the inverse of the per-link insert, never a
cascade onto the asset or target).

It is fail-closed at every step and hidden as `404` for a caller who cannot see the tenant, is not a member of
the asset's workspace, names an asset in another workspace/tenant, or names a link that does not exist (or that
attaches another asset) — and **removing a non-existent link is a safe `404`** (it reveals nothing and changes
nothing). A known workspace member who lacks the unlink role is `403`; asset links are host-prepared content, so
the role set is the host-capable `Owner`/`Admin`/`Host`/`CoHost` (the same set that creates the link and deletes
assets/scenes/entities/content blocks), matched exactly (`MembershipRole` is non-linear). Faithful to the
add-link precedent (CORE-AST-005) and the entity-relationship removal (CORE-LIFE-002), the removal emits no event
and writes no audit record, and the schema is unchanged so no migration is needed.

### Template deletion

An authorized admin can delete an organization-scoped template (CORE-LIFE-008, the "Resource Lifecycle and
Deletion" epic):

| Method   | Route                                                             | Authorized callers              |
| -------- | ----------------------------------------------------------------- | ------------------------------- |
| `DELETE` | `/api/v1/organizations/{organizationSlug}/templates/{templateId}` | organization `Owner` or `Admin` |

The Templates module had a template **create + load** (CORE-ENT-004) but no delete — a template could be
registered and materialized into a workspace's entity types, never removed. This adds the **inverse**, reusing the
existing `ITemplateRepository`. A template is an **organization-level** registry resource (not workspace content),
so the route is org-scoped exactly like the organization member-removal route: the tenant's slug is in the path
(resolved by the same token-claim-and-membership tenant check), and the template is addressed by id within that
tenant. Deleting an org template is the **"authorized admin" action**, so the role set is **organization `Owner`
or `Admin`** (the same admin set the member-removal route uses), matched exactly (`MembershipRole` is non-linear).

**The global vs organization template boundary** (the headline requirement): a **global** template
(`organization_id IS NULL`) is available to every tenant and **cannot be deleted by an organization**, while an
**organization-scoped** template is owned by, and deletable only within, its one tenant. This is enforced
**structurally**, not by a branch: the template is loaded through `FindByOrganizationAndIdAsync`, which matches
only a row whose `organization_id` equals the resolved tenant — a global template is **never** returned through
the org path, so an organization's attempt to delete a global template (even with its exact id) is an
indistinguishable hidden `404` and the global template is left intact. A template owned by another organization is
equally unreachable. The route is org-scoped, so there is no global delete path from this surface at all.

On success the addressed template row is hard-deleted and the route returns `204 No Content`; **only the
`templates` row is removed**. **Already-instantiated entity types are unaffected** (the acceptance criterion): the
loader materializes **normal** workspace `EntityType` rows that carry **no foreign key back to the template**, so
deleting the registry entry leaves every previously loaded type in place — there is nothing to cascade. It is
fail-closed at every step and hidden as `404` for a caller who cannot see the tenant, names a template owned by
another organization or a global template, or names a template that does not exist — and **deleting a
non-existent template is a safe `404`** (it reveals nothing and changes nothing). A known tenant member who lacks
`Owner`/`Admin` is `403`. Faithful to the template-create precedent (CORE-ENT-004) and the entity-relationship
removal (CORE-LIFE-002), the deletion emits no event and writes no audit record, and the schema is unchanged so
no migration is needed.

### Export jobs

The Exports module owns generic, **asynchronous** data exports (`docs/05_MODULE_CONTRACTS.md`:
the Exports module owns "export jobs", "user data export" and "workspace export";
`docs/02_ARCHITECTURE.md`: the worker owns "background jobs, exports, cleanup, async
processing"). CORE-AUD-002 adds the first piece — the `ExportJob` aggregate and its tenant-
and workspace-scoped `export_jobs` table (`apps/api/Exports/`; the documented critical index is
`export_jobs(workspace_id, id)`). It is the **job record only**: the explicit export `scope`,
the lifecycle `status`, the `requested_by` requester and an optional generic `failure_reason`.
The exported data is never stored on the row, and the produced export **manifest** is the later
story (CORE-AUD-003).

The job is **generic and authorized** (the epic acceptance criterion) along two immutable axes.
It is workspace- and tenant-scoped, so every lookup is scoped by organization id then workspace
id (the organization boundary is checked before the workspace boundary) and one workspace's
export job can never be read through another workspace's or another tenant's id (threats T5/T1).
And it carries an **explicit** `ExportScope` — `Workspace` (a full workspace export) or
`UserData` (a single user's own data) — the "explicit host/admin export scopes" control for
threat T8 ("Export leak"); the scope is decided at creation by the requester's authorization and
can never be widened afterwards, so a user-data export is never silently promoted into hidden
workspace content.

A job is registered `Pending` (queued), a worker `Start`s it (`Pending` → `Running`), and it
settles into exactly one **terminal** state — `Completed` on success or `Failed` (with a generic,
log-safe reason) on error — through a guarded state machine: an out-of-order transition is
rejected, not a no-op, so a finished export can never be silently re-run or overwritten. The
optional `requested_by` user foreign key **sets null** on delete, so deleting the requesting user
anonymizes the job record rather than deleting the audit trail of who requested an export
(mirrors `assets.created_by`); the tenant and workspace foreign keys cascade.

The Exports module also defines the **role-based export projection** (the "export role-based
projection" control for threat T8): the full `ExportJobView` for host-capable / metadata roles
(Owner/Admin/Host/CoHost/Auditor — the "View workspace metadata" = yes roles) versus the
stripped, audience-safe `ExportJobSummaryView` (`{id, scope, status}` only) for audience roles,
fail-closed to the summary shape for any undefined role. The projector decides the view **shape**,
not access; the export-request/list HTTP route and its server-side access authorization are a
later Exports story. CORE-AUD-002 is the export job model, its persistence and its EF migration
only; there is no export HTTP route yet.

### Export manifests

CORE-AUD-003 adds the Exports module's **workspace export manifest** — the produced
**table of contents** of a completed workspace export (`docs/05_MODULE_CONTRACTS.md`: the Exports
module owns "export manifests"). CORE-AUD-002 modeled the export **job** and left "the produced
export manifest" to this story; the `ExportManifest` aggregate is that manifest, persisted in the
tenant- and workspace-scoped `export_manifests` table with its per-kind inventory in the
`export_manifest_entries` child table (`apps/api/Exports/`; the documented critical index is
`export_manifests(workspace_id, id)`, with a unique `export_manifests(export_job_id)` — exactly one
manifest per job). A manifest records which `export_job` produced it, the tenant/workspace it
belongs to, the explicit export `scope`, a manifest format `version`, the `generated_at` timestamp
and one `ExportManifestEntry` per generic `ExportResourceKind`
(`Session`/`Scene`/`ContentBlock`/`Entity`/`Participant`/`Asset`) with a **count** — the inventory
of how many resources of each kind the export covered.

The manifest is **generic and authorized** (the epic acceptance criterion) along the same immutable
axes as the job: it is workspace- and tenant-scoped, so every lookup is scoped by organization id
then workspace id (the organization boundary is checked before the workspace boundary) and one
workspace's manifest can never be read through another workspace's or another tenant's id (threats
T5/T1); and it carries the explicit `ExportScope` of the producing job — the workspace export
manifest factory (`ExportManifest.ForWorkspaceExport`) only ever builds a manifest for a
**completed**, **workspace-scoped** export job, so a user-data export is never widened into a
workspace one (threat T8 "explicit host/admin export scopes"). The manifest is **write-once**
(the produced output of a finished export): the aggregate is immutable and the repository exposes
only an append and tenant-scoped reads — no update or delete — exactly as the audit log is
append-only.

The manifest holds identifiers, the scope, a version, a timestamp and per-kind **counts** only —
never the exported data and never any scene/content body (threats T7/T8). Because the per-kind
inventory reveals the shape of a workspace's host content, the Exports module also defines the
**role-based manifest projection** (the "export role-based projection" control for threat T8): the
full `ExportManifestView` (with the inventory) for host-capable / metadata roles
(Owner/Admin/Host/CoHost/Auditor — the "View workspace metadata" = yes roles) versus the stripped,
audience-safe `ExportManifestSummaryView` (`{id, scope}` only — no inventory) for audience roles,
fail-closed to the summary shape for any undefined role. The projector decides the view **shape**,
not access; the worker that drives the export and produces the manifest, and any export HTTP route
with its server-side access authorization, are later Exports stories (exactly as CORE-AUD-002
deferred the export endpoint). CORE-AUD-003 is the manifest model, its persistence, its EF
migration and its role-based projection only; there is no export HTTP route yet.

### Recaps

The Recaps module owns generic session **recaps** (`docs/03_DOMAIN_LANGUAGE.md`: a Recap is a
"session summary or structured continuation output"; `docs/05_MODULE_CONTRACTS.md`: the Recaps
module owns "session recaps"; `docs/00_START_HERE.md`: a Host can "stream SessionEvents and
produce Recaps"). CORE-AUD-004 (the `Audit, Export and Recap` epic) adds the first piece — the
`Recap` aggregate and its session-scoped `recaps` table (`apps/api/Recaps/`; the documented
critical index is `recaps(workspace_id, id)`). A recap records which `session` it summarizes, the
tenant (`organization_id`) and workspace (`workspace_id`) it belongs to, the optional producing
user (`generated_by`), a recap format `version`, the generic `summary` body and the `generated_at`
timestamp. It stores no vertical product language — a generic session recap only, never a
"session debrief report" (`csv/forbidden_core_terms.csv`).

The recap is **generic and authorized** (the epic acceptance criterion). It is session-, workspace-
and tenant-scoped, so every lookup is scoped by organization id then workspace id (the organization
boundary is checked before the workspace boundary) and one workspace's recap can never be read
through another workspace's or another tenant's id (threats T5/T1). A recap may be produced by a
**Host** or by the **system** (`docs/09_EVENT_CATALOG.md`: `RecapGenerated` source "System/Host"),
so the `generated_by` user foreign key is **nullable** and **sets null** on delete — deleting the
producing user anonymizes the recap rather than deleting it (mirrors `export_jobs.requested_by`);
the tenant, workspace and session foreign keys cascade. A recap is **write-once** — the aggregate is
immutable and the repository exposes only an append and tenant-scoped reads (no update or delete),
exactly as the audit log is append-only.

The recap `summary` is **host content**. The event catalog records that a generated recap is
"Participant-visible only after separate reveal" (`docs/09_EVENT_CATALOG.md`), so the Recaps module
defines the **role-based recap projection** (threat T2 visibility leak): the full `RecapView` (with
the body) for host-content / metadata roles (Owner/Admin/Host/CoHost/Auditor) versus the stripped,
audience-safe `RecapSummaryView` (`{id, sessionId}` only — no body) for audience roles, fail-closed
to the summary shape for any undefined role. The body is also kept out of logs: the aggregate's
identifier-only `ToString` renders a coarse length, never the body (threat T7). The projector decides
the view **shape**, not access; the recap-generation command (the producer that writes a recap and
emits `RecapGenerated`), the separate participant reveal, and any recap HTTP route with its
server-side access authorization are later stories (exactly as CORE-AUD-002/003 deferred the export
endpoint). CORE-AUD-004 is the recap model, its persistence, its EF migration and its role-based
projection only; there is no recap HTTP route yet.

### Entitlement and plan definitions

The Entitlements module owns the Core's product-neutral monetization catalog so that usage limits and premium
capabilities are enforced **server-side** and cannot be bypassed by a mobile client
(`docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`: "Limits ... must be enforced server-side. Otherwise users
can bypass mobile UI restrictions"). CORE-ENTL-001 (the first story of the `Entitlements and Quotas` epic) adds
the first piece — the **definition model**: the `EntitlementDefinition` and `PlanDefinition` aggregates and
their three **global** tables (`apps/api/Entitlements/`; `csv/database_tables.csv`: module Entitlements, scope
`global`).

An `EntitlementDefinition` is the catalog entry for one generic entitlement: a stable, lower-case dotted `key`
(such as `workspace.active.max` or `ads.disabled` — `docs/21` "Generic entitlement keys"), a `value_kind`
(`Flag` for a boolean capability or `Quota` for a numeric limit, mirroring `csv/mobile_entitlement_catalog.csv`'s
`type` column), generic display metadata and a soft-lifecycle `is_active` flag. A `PlanDefinition` is a named
bundle: a stable `key`, generic display metadata, `is_active`, and a child collection of `plan_entitlements`
**grants** that bind an entitlement definition to a concrete value. The grant's value shape is fixed by the
referenced definition's `value_kind` — a flag grant carries a boolean (`flag_value`) and a quota grant carries
a numeric `quota_limit` (or `null` for an unlimited/fair-use grant) — so a plan can never bind the wrong value
shape, and an entitlement is granted **at most once per plan** (the unique
`plan_entitlements(plan_definition_id, entitlement_definition_id)` index). The granted value is always decided
**server-side**, never trusted from a client (`docs/21` "Never trust client-side premium flags").

**Generic — the epic acceptance criterion** ("Generic entitlements can be defined without vertical
terminology"). Every key, display name and description is generic Core vocabulary only (AGENTS.md,
`csv/forbidden_core_terms.csv`); a vertical maps a key to its own paywall copy in its UI (`docs/21` "ArcanOS may
display these as ..."). The specific commercial plans of a vertical — and their concrete values, which `docs/21`
lists only as a "Recommended ... mapping" of "examples ... to be finalized" — are vertical **seed data**
supplied by the vertical (`docs/04_PRODUCT_BOUNDARIES.md`), never hardcoded in Core source.

These are **global** catalog tables (the deployment-wide catalog, like `organizations`/`users`/`templates`), so
none carries an `organization_id`: there is no per-tenant copy, no host-only body and no audience to project
away, so unlike the recap/export models there is **no** tenant-scoped or role-based projection in this story.
Definitions are **business data** that grants (and later subject assignments) reference, so they are never
hard-deleted — they are soft-retired via `is_active` (`docs/10_DATABASE_SCHEMA.md`: "avoid hard-delete for
business data; use soft delete where needed"), the `plan_definition_id` grant foreign key cascades and the
`entitlement_definition_id` grant foreign key is **restricted** so a referenced definition can never be deleted
out from under a grant. The `key` of each definition is immutable, so an entitlement or plan key can never
silently change meaning.

CORE-ENTL-001 is the definition model, its persistence and its EF migration only. The per-subject assignment
and lookup ("user-visible premium state comes only from server entitlements", CORE-ENTL-002), the quota
definition and quota-status API (CORE-ENTL-003) and quota enforcement on protected workspace/session commands
(CORE-ENTL-004) are later stories; there is no entitlement HTTP route yet
(`csv/mobile_store_api_routes.csv` defines the `GET /v1/me/entitlements` read for a later story).

### Subject entitlements

CORE-ENTL-002 (the next story of the `Entitlements and Quotas` epic) adds the **per-subject assignment** of the
catalog entitlements and the **server-side lookup** that resolves them into a subject's premium state — the
acceptance criterion **"User-visible premium state comes only from server entitlements"**
(`docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`: "Never trust client-side premium flags";
"User-visible premium state must come from server entitlements"). CORE-ENTL-001 modeled the global catalog
(_what_ entitlements and plans exist); this story records _which subject holds which entitlement at which value_
in the `subject_entitlements` table (`apps/api/Entitlements/`; module Entitlements,
`csv/database_tables.csv`).

A `SubjectEntitlement` records that one **generic subject** — an `EntitlementSubjectType.User`
(the "current user" of `GET /v1/me/entitlements`) or an `EntitlementSubjectType.Workspace` (the subject of a
workspace-scoped entitlement) — holds a granted `EntitlementDefinition` at a concrete value. Its value shape is
fixed by the definition's `value_kind` exactly like a plan grant (a flag carries `flag_value`, a quota carries
`quota_limit` — `null` meaning an unlimited/fair-use grant), so an assignment can never bind the wrong value
shape. The row is **self-describing**: the stable, immutable `entitlement_key` is denormalized onto it as a
recorded fact (so the hot-path lookup needs no join), while the `entitlement_definition_id` foreign key still
**restricts** hard-deleting a referenced definition (it is soft-retired via `is_active` instead). The optional
`source_plan_definition_id` records the plan an assignment was granted from (provenance, **set null** on the
rare plan delete).

The assignment is **per-subject, not tenant content**: it carries no `organization_id` and is keyed by the
`(subject_type, subject_id)` pair. The subject id is a generic **polymorphic** reference (no database foreign
key, mirroring `asset_links.target_id` / `visibility_rules.resource_id` / `session_events.visibility_subject_id`),
so a user subject and a workspace subject that share a guid never collide, and one subject's premium state can
never be read through another subject's id. A subject holds each entitlement **at most once** (the unique
`subject_entitlements(subject_type, subject_id, entitlement_definition_id)` index, whose
`(subject_type, subject_id)` prefix is the critical lookup index).

Premium state is resolved **server-side and fail-closed** by `SubjectEntitlementResolver`, which reads **only**
a subject's **active** assignments into `EffectiveEntitlements` — the single source of premium state, used
identically by a later `GET /v1/me/entitlements` response and any internal feature guard so the two can never
diverge. A subject with no active assignment for an entitlement is simply **not entitled** (`IsFlagEnabled`
defaults to `false`; `TryGetQuotaLimit` reports "no entitlement"), and a **revoked** assignment (a refund,
cancellation or downgrade — `docs/21` "Refunds and chargebacks must revoke or downgrade entitlements") is
excluded by the active-only read, so a client can never obtain premium state the server did not grant. The
`EffectiveEntitlement` view handed out carries only the generic key and value — no subject id, no internal ids,
no source-plan provenance, no authorization rationale (a vertical maps the key to its own paywall copy in its
UI).

The `SubjectEntitlementAssignmentService` is the write side: `AssignFromPlanAsync` grants a subject every
entitlement of a plan at the plan's server-decided values (**reusing** the CORE-ENTL-001 plan catalog — the
client never supplies a premium value), idempotently (a re-run updates existing assignments in place and
reinstates revoked ones rather than duplicating; a retired plan or a retired entitlement is never newly
assigned, fail-closed), and `RevokeAsync` revokes a single entitlement for the downgrade/refund path. The
service performs the assignment mechanism only; the purchase-verification flow that _triggers_ an assignment and
the store-notification downgrade flow that _triggers_ a revocation are later stories — this story supplies the
generic, reusable assignment, revocation and lookup primitives they build on. The `GET /api/v1/me/entitlements`
HTTP route that _exposes_ the resolved state (with its authenticated, own-subject-only authorization) is now
implemented (CORE-API-007; see "Current-user entitlements" below).

### Quota definitions and quota status

CORE-ENTL-003 (the next story of the `Entitlements and Quotas` epic) adds the **quota definition** and the
**server-side quota-status calculation and API** — the acceptance criterion **"Quota status is calculated
server-side for subjects and workspaces"** (`docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`: "Limits such as
active workspace count, active session count, participant count, storage ... must be enforced server-side").
CORE-ENTL-001/002 modeled _what_ entitlements exist and assigned a subject its _limit_; this story adds _how_ a
numeric quota is measured and _how much is left_, in two new tables (`apps/api/Entitlements/`; module
Entitlements, `csv/database_tables.csv`).

A `QuotaDefinition` (the global `quota_definitions` catalog, no `organization_id`) says how a numeric
`EntitlementValueKind.Quota` entitlement is measured: the measured `entitlement_definition_id` (with its
denormalized, immutable `entitlement_key` recorded as a fact, and a **restricted** foreign key so the measured
definition can't be hard-deleted), the `subject_type` the usage is counted for (`User` → surfaced on
`/me/quota-status`; `Workspace` → surfaced on `/workspaces/{workspaceId}/quota-status`) and the `unit` (`Count`
or `Bytes`). Only a **quota** entitlement can be measured — a flag (boolean capability) has no usage to count and
is rejected — and a quota is soft-retired via `is_active`, never hard-deleted. A `QuotaUsage` (the per-subject
`quota_usage` table, keyed by the `(subject_type, subject_id)` pair exactly like `subject_entitlements`, the
subject id a generic polymorphic reference) records a subject's current `used_amount` of a quota; a subject
records each quota **at most once** (the unique `quota_usage(subject_type, subject_id, quota_definition_id)`
index).

The **status is calculated server-side and fail-closed** by `QuotaStatusCalculator`, the single place the quota
math lives: for each active quota defined for the subject's kind it combines the subject's **granted limit**
(resolved from its active entitlements through the CORE-ENTL-002 `SubjectEntitlementResolver` — **reused**, not
re-derived) with its **recorded usage** (a missing usage row reads as zero). The boundary semantics: usage
exactly **at** the cap is allowed (not exceeded, zero remaining); usage **over** the cap is exceeded with zero
remaining (never negative); an **unlimited** (fair-use) grant is never exceeded and has no finite remaining; and a
subject **not entitled** to a defined quota has **no allowance** — the limit is zero, so any usage already exceeds
it (a client can never obtain headroom the server did not grant; `docs/21` "Never unlock limits before server
verification succeeds").

Two HTTP routes expose the calculated status (under the Core `/api/v1` prefix `docs/08_API_CONTRACTS.md` mandates;
`csv/api_routes.csv`, `csv/mobile_store_api_routes.csv`):

| Method | Route                                           | Authorized callers                             |
| ------ | ----------------------------------------------- | ---------------------------------------------- |
| `GET`  | `/api/v1/me/quota-status`                       | any authenticated user (their own status)      |
| `GET`  | `/api/v1/workspaces/{workspaceId}/quota-status` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

`/me/quota-status` resolves the **current user's** profile (the canonical, idempotent "current user" resolution)
and calculates the `User` subject's status; a **service account** has no personal quota and is `403`.
`/workspaces/{workspaceId}/quota-status` resolves the target organization from a required `?organizationSlug=`
(token claim **and** persisted membership), then requires the caller to be a **member** of that workspace — a
caller who cannot see the tenant, and a non-member, are hidden as `404` (never `403`); a known member who is not a
host-capable role is `403`, since a workspace's limits/usage are management metadata. The response is
**client-safe**: only the generic quota key, unit and computed numbers — no subject id, no internal ids, no
authorization rationale (a vertical maps the key to its own paywall copy in its UI). **Enforcing** quotas on
protected workspace/session commands (incrementing usage and rejecting over-limit) is the next story
(CORE-ENTL-004, below).

### Quota enforcement on protected commands

CORE-ENTL-004 (the final story of the `Entitlements and Quotas` epic) turns the recorded usage and status
calculation into **server-side enforcement** at the protected commands — the acceptance criterion **"Free limits
cannot be bypassed by clients"** (`docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`: "Limits ... must be enforced
server-side. Otherwise users can bypass mobile UI restrictions"). The Entitlements module's
`QuotaEnforcementService` is the reusable gate: a protected command asks it whether a subject may consume one unit
of a quota **before** doing any work, and records the consumption **only after** the work succeeds (a command that
frees a counted resource releases the unit). The decision **reuses** the single `QuotaStatus.Calculate` math the
quota-status read uses, so a command's allow/deny can never diverge from what `GET …/quota-status` reports, and it
is **fail-closed**: a subject not entitled to a defined quota has no allowance, and the decision is computed
entirely server-side from the catalog limit and recorded usage — a client supplies no part of it.

| Command                            | Quota key (generic)    | Quota subject           | Behavior                                      |
| ---------------------------------- | ---------------------- | ----------------------- | --------------------------------------------- |
| `POST /api/v1/workspaces`          | `workspace.active.max` | the creating user       | increments on create                          |
| `POST /api/v1/sessions/{id}/start` | `session.active.max`   | the session's workspace | increments on start                           |
| `POST /api/v1/sessions/{id}/end`   | `session.active.max`   | the session's workspace | releases (decrements, clamped at zero) on end |

The check runs **after** the command's existing role/tenant authorization (so quota state is never consulted for an
unauthorized caller) and **after** the session state guard, so an unauthorized caller (`403`/`404`) or an
out-of-state transition (`409`) is rejected without ever consuming the quota. A command that would exceed the
subject's limit is rejected with **`409 Conflict`** whose detail names only the generic quota key (the same key the
quota-status read returns, so a vertical can map it to paywall copy) and leaks no internal id or rationale. Core
enforces only quotas that **exist**: when a deployment defines no matching `QuotaDefinition`, the command proceeds
unchanged — a deployment that wants a free limit defines the quota definition and grants the free entitlement. No
new HTTP route, table or migration is added; enforcement reads and writes the existing `quota_definitions` /
`quota_usage` tables.

### Ad eligibility

CORE-ADS-001 (the `Ad Eligibility` epic) decides whether a subject is eligible to see ads — the two Core-owned ad
types of `docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md`, `AdEligibilityPolicy` and `AdEligibilityResult`, plus the
read that exposes them. It lives in the Entitlements module (ad eligibility is **entitlement-driven**) and reuses the
CORE-ENTL-002 `SubjectEntitlementResolver`, so it adds **no table and no migration**. Core decides eligibility only;
it never renders, requests, configures or places ads (`docs/22`).

| Method | Route                       | Authorized callers                                 |
| ------ | --------------------------- | -------------------------------------------------- |
| `GET`  | `/api/v1/me/ad-eligibility` | any authenticated **user** (their own eligibility) |

The response is the generic, entitlement-derived decision and nothing else — no ad placement, ad provider/unit id or
SDK config (the epic acceptance criterion **"Core returns ad eligibility without knowing ad placements"**; threat
T7):

```json
{
    "adsRequired": true,
    "reason": "NO_AD_FREE_ENTITLEMENT",
    "sessionAdFreeUntil": null,
    "hostedSessionAdFree": false
}
```

`AdEligibilityPolicy.Evaluate` is a **pure, fail-closed** function of the user's resolved effective entitlements: ads
are required by default and turned off only by an explicit, active server grant of `ads.disabled` (the personal
ad-free state) — so a client can never assert ad-free state the server did not grant ("Never trust client-side
premium flags"). Otherwise an explicit `ads.required` grant yields `reason: ADS_REQUIRED_ENTITLEMENT`, and a subject
with no relevant grant yields `reason: NO_AD_FREE_ENTITLEMENT`. `hostedSessionAdFree` is reported **independently**
from the `hosted.sessions.ads.disabled` capability (sessions the subject hosts are ad-free for participants) and does
not change the subject's own `adsRequired`; `sessionAdFreeUntil` is part of the contract shape for a future,
mobile-driven temporary (rewarded-ad) window and is currently always `null`.

`/me/ad-eligibility` resolves the **current user's** profile (the canonical, idempotent "current user" resolution)
and decides for the `User` subject keyed by the profile id, reading only that subject's entitlements (per-subject
isolation, threat T5). A missing/invalid token is `401`; a non-user **service-account** principal is `403` (it has
no personal premium state, the same rule as the `/me/quota-status` read); the route carries no tenant boundary, and
fails closed with `503` when no database is configured.

### Current-user entitlements

CORE-API-007 (the `API Completeness` epic) exposes the documented-not-built
`GET /v1/me/entitlements` read (`csv/mobile_store_api_routes.csv`) under the Core `/api/v1` prefix. It is the
read half of the entitlements story (CORE-ENTL-002) made reachable over HTTP: it reuses the
`SubjectEntitlementResolver` unchanged, so it adds **no table and no migration** and can never diverge from an
internal feature guard that consults the same resolver.

| Method | Route                     | Authorized callers                                  |
| ------ | ------------------------- | --------------------------------------------------- |
| `GET`  | `/api/v1/me/entitlements` | any authenticated **user** (their own entitlements) |

The response is the current user's resolved, server-authoritative effective entitlements — the generic key and
value only, ordered by key, with no subject id, internal surrogate id, source-plan provenance or authorization
rationale (the epic acceptance criterion **"User-visible premium state comes only from server entitlements"**;
threat T7). Each item carries the generic `key`, the `valueKind` (`Flag` or `Quota`), the granted `flagValue`
(for a flag) and the granted `quotaLimit` (for a quota — `null` meaning an unlimited/fair-use grant):

```json
{
    "entitlements": [
        {
            "key": "ads.disabled",
            "valueKind": "Flag",
            "flagValue": true,
            "quotaLimit": null
        },
        {
            "key": "workspace.active.max",
            "valueKind": "Quota",
            "flagValue": null,
            "quotaLimit": 5
        }
    ]
}
```

It resolves the **current user's** profile (the canonical, idempotent "current user" resolution) and resolves the
`User` subject keyed by the profile id, reading only that subject's active assignments (per-subject isolation,
threat T5) — so a revoked or never-granted entitlement is simply absent (the fail-closed default) and one user's
premium state is never returned through another's id. A missing/invalid token is `401`; a non-user
**service-account** principal is `403` (it has no personal premium state, the same rule as the `/me/ad-eligibility`
and `/me/quota-status` reads); the route carries no tenant boundary, and fails closed with `503` when no database
is configured.

### Purchase provider abstraction

The Store module owns generic, server-side **store purchase verification** so that the client never becomes the
source of truth for premium access (`docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md` "Receipt verification":
the backend verifies a proof with Apple/Google server APIs **before** granting any entitlement). CORE-STORE-001
(the first story of the `Store Purchase Verification` epic) adds the first piece — the **provider abstraction**,
whose acceptance criterion is that **Apple/Google provider logic is isolated from Core domain logic**.

`IPurchaseVerificationProvider` (`apps/api/Store/`) is the single **port** between Core and a store's own server
APIs: one adapter serves one `PurchaseProvider` (`Apple` or `Google` — infrastructure provider names allowed here
per `docs/21`/`docs/22`, never vertical vocabulary) and reduces that store's raw response to a **provider-neutral**
`PurchaseVerificationResult` — a normalized `VerifiedPurchase` (provider + provider transaction id + product
reference) on success, or a generic, log-safe rejection otherwise. Core domain logic builds one neutral
`PurchaseVerificationRequest` (the provider plus the **opaque** proof — a transaction token / JWS / purchase token
Core never parses or trusts) and branches on one neutral result, so the provider differences live entirely behind
the port. The submitted proof is a secret: `PurchaseVerificationRequest.ToString` excludes it, so an unverified,
possibly forged token is never logged (threat T7).

Like the S3-compatible `IAssetStorage` adapter (CORE-AST-002) and the Valkey/Redis `IRealtimeBackplane`
(CORE-RT-006), the concrete, **credential-bearing** verification adapters (the store SDK and keys) are supplied by
the deployment (`docs/13_SELF_HOSTING_REQUIREMENTS.md`; threat T7) — Core carries no native store SDK dependency
and no store credentials. The `PurchaseVerificationProviderResolver` selects an adapter by the generic
`PurchaseProvider`, and it is **fail-closed**: Core registers no adapter, so every provider throws
`PurchaseProviderNotConfiguredException` until a deployment wires one — Core never trusts a client's unverified
proof and never grants premium state without a real verification ("Never unlock limits before server verification
succeeds", `docs/21`). This is the purchase-verification analogue of the fail-closed `UnconfiguredAssetStorage`.

CORE-STORE-001 is the abstraction (the port, its provider-neutral value types and the fail-closed resolver) only;
there is **no** store HTTP route, table or migration yet. Persisting the verified transaction and its audit trail
(CORE-STORE-002), the Apple (CORE-STORE-003) and Google (CORE-STORE-004) verification endpoint contracts, and
idempotent store notifications (CORE-STORE-005) are later stories.

### Purchase transaction persistence and audit trail

CORE-STORE-002 (the next story of the `Store Purchase Verification` epic) persists the verified purchase
CORE-STORE-001 produced and records its lifecycle as an audit trail — the acceptance criterion **"Purchase state
changes are persisted and auditable"** (`docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`: "Backend persists
PurchaseTransaction"; "All purchase state changes must be auditable"). CORE-STORE-001 modeled the provider
abstraction and explicitly deferred "persistence of the verified transaction" to here. It adds two of the Store
module's tables (`apps/api/Store/`; `csv/database_tables.csv`: module Store) and the recording service over them;
there is still **no** store HTTP route (the verification endpoints are CORE-STORE-003/004).

A `PurchaseTransaction` is the persisted record of one verified purchase: the `provider`, the provider-assigned
`provider_transaction_id`, the `product_reference`, the current lifecycle `status` and the record/update
timestamps. It is built **only** from a `VerifiedPurchase` a provider adapter already verified server-side, so Core
never trusts a client (`docs/21` "Never trust client-side premium flags"). The buyer/subject linkage — which user
or workspace the purchase grants premium to — is the separate `billing_account_links` table (a later story), not a
column here, so the transaction carries no `organization_id` and no buyer column. The stored identifiers are not
secrets; only the raw verification proof is, and the proof is **never persisted** (threat T7).

The purchase is named **idempotently** by the `(provider, provider_transaction_id)` pair — a provider transaction
id is unique within its provider — so the unique `purchase_transactions(provider, provider_transaction_id)` index
makes recording the same verified purchase twice (a client retry, a replayed proof, a duplicate notification) a
safe no-op that creates no second row and no duplicate audit event (`docs/21` "Store notifications must be
idempotent"), the persistence analogue of the unique `idempotency_keys(scope, key)` index.

`PurchaseEvent` is the append-only `purchase_events` trail (mirrors the append-only `audit_logs` and
`session_events`): each row records one state change as a `previous_status` (NULL for the initial recording) →
`new_status` pair, so a purchase's lifecycle is fully reconstructable from immutable facts. The
`purchase_transaction_id` foreign key **cascades** — the trail is part of the transaction's own history; the
documented critical index is `purchase_events(purchase_transaction_id, created_at)`.

A verified purchase is recorded `Active`; the generic states `Active`/`Cancelled`/`Refunded`/`InGracePeriod` model
renewals, cancellations, refunds and grace periods (grace periods represented **explicitly**, `docs/21`).
`PurchaseTransactionService` records a verified purchase (writing the initial audit event) and audits each status
change, idempotently — recording an already-recorded purchase returns `AlreadyRecorded` and a change to the current
status writes no event, and a change to an unknown purchase fails closed. **Which** provider notification drives
**which** transition, the idempotent ingestion of those notifications and the entitlement downgrade/revocation a
refund or cancellation causes are the later store-notification story (CORE-STORE-005); authorization is upstream of
this service (the Apple CORE-STORE-003 and Google CORE-STORE-004 verification endpoints authorize the caller and
verify the proof **before** recording). This story supplies the generic, reusable persistence + audit primitives
they build on.

### Apple transaction verification endpoint

CORE-STORE-003 wires the provider abstraction (CORE-STORE-001) and the persistence + audit primitives
(CORE-STORE-002) into the Store module's first **HTTP route**, so that **Apple transaction data is verified
before entitlements are granted** (the story's acceptance criterion):

| Method | Route                                  | Authorized callers                                   |
| ------ | -------------------------------------- | ---------------------------------------------------- |
| `POST` | `/api/v1/purchases/apple/transactions` | any authenticated user (a service account is denied) |

The request body carries only the opaque Apple App Store **signed transaction (JWS) / transaction proof** and an
optional opaque product reference; Core never parses, trusts or logs the proof (it is carried verbatim into a
provider-neutral `PurchaseVerificationRequest`). The flow is **verify-then-record, fail-closed**: the endpoint
authorizes the caller, resolves the deployment-supplied Apple adapter through `PurchaseVerificationProviderResolver`
and verifies the proof, and **only a verified result** is persisted as a `PurchaseTransaction` (reusing the
CORE-STORE-002 `PurchaseTransactionService`, so recording is idempotent — a retry or a replayed-but-genuine proof
creates no second row and no duplicate audit event). A rejected (forged / replayed / unverifiable) proof is `422`
and records **nothing**; when no Apple adapter is configured the resolver fails closed and the request is `503`
(the verification analogue of the unconfigured asset storage). So Core never trusts a client's premium claim and
never grants premium state without a real server-side verification behind it.

Submitting a purchase is an inherently **per-user** action (the buyer's own receipt), so a missing/invalid token
is `401` and a non-user **service-account** principal is `403` (the same rule as the `/me` quota-status read). The
transaction is named **globally** by its `(provider, provider_transaction_id)` pair and carries **no tenant**
(CORE-STORE-002: `purchase_transactions` has no `organization_id`), so there is no organization/workspace boundary
on this route; the body is validated only **after** authorization, so an unauthorized caller never receives
request-shape feedback. Granting the resulting `SubjectEntitlement` from the recorded purchase (the product → plan
→ entitlement mapping) and linking the buyer (`billing_account_links`) are later stories; the Google
purchase-token endpoint is CORE-STORE-004 and idempotent store notifications are CORE-STORE-005. This story adds
no new table and no EF migration (it reuses `purchase_transactions` and `purchase_events`).

### Google purchase token verification endpoint

CORE-STORE-004 adds the **Google** side of the receipt-verification flow — the Google analogue of the Apple
endpoint above — so that **Google purchase tokens are verified before entitlements are granted** (the story's
acceptance criterion):

| Method | Route                             | Authorized callers                                   |
| ------ | --------------------------------- | ---------------------------------------------------- |
| `POST` | `/api/v1/purchases/google/tokens` | any authenticated user (a service account is denied) |

The request body carries only the opaque Google Play **purchase token** and an optional opaque product reference
(a Google Play product/SKU); Core never parses, trusts or logs the token (it is carried verbatim into a
provider-neutral `PurchaseVerificationRequest`). The flow is **verify-then-record, fail-closed**: the endpoint
authorizes the caller, resolves the deployment-supplied Google adapter through
`PurchaseVerificationProviderResolver` and verifies the token, and **only a verified result** is persisted as a
`PurchaseTransaction` (reusing the CORE-STORE-002 `PurchaseTransactionService`, so recording is idempotent — a
retry or a replayed-but-genuine token creates no second row and no duplicate audit event). A rejected (forged /
replayed / unverifiable) token is `422` and records **nothing**; when no Google adapter is configured the resolver
fails closed and the request is `503` (the verification analogue of the unconfigured asset storage). So Core never
trusts a client's premium claim and never grants premium state without a real server-side verification behind it.

Submitting a purchase is an inherently **per-user** action (the buyer's own receipt), so a missing/invalid token
is `401` and a non-user **service-account** principal is `403` (the same rule as the Apple endpoint and the `/me`
quota-status read). The transaction is named **globally** by its `(provider, provider_transaction_id)` pair and
carries **no tenant** (CORE-STORE-002: `purchase_transactions` has no `organization_id`), so there is no
organization/workspace boundary on this route; the body is validated only **after** authorization, so an
unauthorized caller never receives request-shape feedback. Granting the resulting `SubjectEntitlement` from the
recorded purchase and linking the buyer (`billing_account_links`) are later stories; idempotent store
notifications are CORE-STORE-005. This story adds no new table and no EF migration (it reuses
`purchase_transactions` and `purchase_events`).

### Store notifications

CORE-STORE-005 adds the Store module's idempotent **store-notification handling** — the realization of step 7 of
the receipt-verification flow ("Store server notifications update entitlement state on renewals, cancellations,
refunds and grace periods") — so that **renewals, cancellations, refunds and grace periods update entitlements
safely** (the story's acceptance criterion):

| Method | Route                                     | Authorized callers                         |
| ------ | ----------------------------------------- | ------------------------------------------ |
| `POST` | `/api/v1/store-notifications/apple`       | none — an Apple server-to-server callback  |
| `POST` | `/api/v1/store-notifications/google/rtdn` | none — a Google RTDN Pub/Sub push callback |

Unlike the verification routes these are **unauthenticated** server-to-server callbacks
(`csv/mobile_store_api_routes.csv`: `auth_required=false`), mapped `AllowAnonymous`. A store notification endpoint
carries no OIDC token, so the **only** thing that makes an inbound payload trustworthy is the deployment-supplied
`IStoreNotificationParser` adapter validating its **signature/source** — the notification analogue of the
`IPurchaseVerificationProvider` port (CORE-STORE-001): one adapter per provider, it validates the opaque raw
payload and reduces it to a provider-neutral `StoreNotification` (provider + the store's unique notification id +
the actionable type + the affected purchase's provider transaction id). The concrete, credential-bearing validators
(signing keys / source verification) are **deployment-supplied** (`docs/13_SELF_HOSTING_REQUIREMENTS.md`; threat
T7); Core carries no store SDK and no signing keys. Until one is wired the `StoreNotificationParserResolver`
**fails closed**, so an inbound notification is `503` and **never changes a purchase without a real validator
behind it**. A forged/unparseable payload is `400` (records nothing); an authentic but non-actionable payload is
acknowledged `200` (records nothing).

Handling is **idempotent** ("Store notifications must be idempotent") in two layers: the **dedup ledger** — the
append-only `store_notification_events` table, keyed by the unique
`store_notification_events(provider, provider_notification_id)` index — recognizes a re-delivered notification and
ignores it with no second effect (the same idempotency shape as `purchase_transactions(provider,
provider_transaction_id)`); and the **idempotent effect** — the purchase status change it drives **reuses**
`PurchaseTransactionService.ChangeStatusAsync` (CORE-STORE-002), which writes no purchase event for a no-op
transition. The notification's actionable type maps to exactly one target purchase status: a **renewal**
keeps/reactivates `Active`, a **cancellation** downgrades to `Cancelled`, a **refund/chargeback** revokes to
`Refunded`, and a **grace period** moves to the explicit `InGracePeriod` state. The persisted purchase status is
the **server-side source of truth** for premium state, so updating it is the safe entitlement update; it is
audited twice over (the `purchase_events` trail for the purchase-side change and the append-only
`store_notification_events` row for the notification's arrival/effect). A notification for a purchase Core never
recorded is `TransactionNotFound` — nothing is fabricated — but its arrival is still recorded so it is auditable
and not reprocessed. The row stores only **normalized identifiers**, never the raw notification body (which may
embed signed receipt content — threat T7). Granting/revoking the linked `SubjectEntitlement` (which requires the
buyer linkage `billing_account_links` plus the product → plan → entitlement mapping) is a later story that consumes
this purchase status as its trigger.

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

Run the worker container (no ports; runs the asset cleanup job when a database is configured,
otherwise idles):

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
