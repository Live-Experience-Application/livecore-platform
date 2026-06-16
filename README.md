# livecore-platform

[![CI](https://github.com/Live-Experience-Application/livecore-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/Live-Experience-Application/livecore-platform/actions/workflows/ci.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL%20v3-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Node.js](https://img.shields.io/badge/Node.js-22-339933.svg)](https://nodejs.org/)
[![pnpm](https://img.shields.io/badge/pnpm-10-F69220.svg)](https://pnpm.io/)
[![Packages](https://img.shields.io/badge/packages-0.1.0-blue.svg)](CHANGELOG.md)

Generic Core Platform for live, role-aware, scene-based interactive sessions.

This repository must stay product-neutral. It must not contain ArcanOS, Pen-and-Paper, DnD, Enterprise or ScenarioOS domain language in source code.

## Overview

LiveCore is a self-hostable platform for controlled, interactive live sessions. A
**Host** prepares a **Workspace**, creates **Sessions**, organizes **Scenes**,
manages **Participants**, defines **ContentBlocks** and **Entities**, applies
**VisibilityRules**, executes **Reveals**, streams **SessionEvents** and produces
**Recaps** — deciding what information is visible to which participant, when, and
in what context.

It is a reusable **Live Experience Engine**: one product-neutral foundation that
multiple vertical products build on. The Core owns the platform (API, realtime
hub, domain model, persistence, the visibility and reveal engines, assets, the
audit log, entitlements/quotas and the typed TypeScript contracts and SDK); each
vertical adds its own domain language on top, never the other way around. The
Core therefore carries no vertical terminology of its own — see
[Owns](#owns) and [Does not own](#does-not-own).

The platform is **production-oriented**: server-side authorization, OIDC
authentication, optimistic concurrency, transactional event publishing,
observability (metrics, structured logging, distributed tracing), supply-chain
gating and Docker/self-hosting readiness are part of the foundation rather than
afterthoughts. Mobile monetization — server-side purchase verification,
entitlements, quotas and ad eligibility — is in scope for v1, while paywalls,
storefronts and ad rendering stay in the vertical apps
([Mobile-related Core extension](#mobile-related-core-extension)).

New to the repository? Read [Start here](#start-here) for the document reading
order, then use [Quick start](#quick-start) to build and run it. The published
TypeScript packages are released together (lockstep); see [`CHANGELOG.md`](CHANGELOG.md).

## Table of contents

- [Overview](#overview)
- [Quick start](#quick-start)
- [Owns](#owns)
- [Does not own](#does-not-own)
- [Start here](#start-here)
- [Repository layout](#repository-layout)
- [Mobile-related Core extension](#mobile-related-core-extension)
- [Prerequisites](#prerequisites)
- [Build, format, lint, test and boundary scan](#build-format-lint-test-and-boundary-scan)
    - [.NET solution (API, worker, smoke tests)](#net-solution-api-worker-smoke-tests)
    - [Code coverage and the coverage gate](#code-coverage-and-the-coverage-gate)
    - [TypeScript packages](#typescript-packages)
    - [TypeScript contract package](#typescript-contract-package)
    - [TypeScript SDK package](#typescript-sdk-package)
    - [TypeScript design tokens package](#typescript-design-tokens-package)
    - [TypeScript UI core package](#typescript-ui-core-package)
    - [Package versioning and changelog](#package-versioning-and-changelog)
    - [Boundary scan](#boundary-scan)
    - [Spec consistency check](#spec-consistency-check)
- [Run the hosts locally](#run-the-hosts-locally)
    - [Deploy the whole stack with Docker Compose](#deploy-the-whole-stack-with-docker-compose)
- [Operations and observability](#operations-and-observability)
    - [Health endpoints](#health-endpoints)
    - [Metrics endpoint](#metrics-endpoint)
    - [Source offer endpoint (AGPL section 13)](#source-offer-endpoint-agpl-section-13)
    - [Worker metrics and per-loop liveness](#worker-metrics-and-per-loop-liveness)
    - [Structured logging](#structured-logging)
    - [Distributed tracing](#distributed-tracing)
- [Identity, persistence and migrations](#identity-persistence-and-migrations)
    - [Identity (OIDC principal model)](#identity-oidc-principal-model)
    - [Persistence (user profile reference)](#persistence-user-profile-reference)
    - [Applying migrations (deployment step)](#applying-migrations-deployment-step)
- [Runtime resilience and edge](#runtime-resilience-and-edge)
    - [Optimistic concurrency](#optimistic-concurrency)
    - [Transactional unit of work (commit-then-publish)](#transactional-unit-of-work-commit-then-publish)
    - [Database connection resilience (retry on transient failures)](#database-connection-resilience-retry-on-transient-failures)
    - [Concurrency conflicts in the worker job contexts](#concurrency-conflicts-in-the-worker-job-contexts)
    - [Atomic quota check-and-consume (no TOCTOU race)](#atomic-quota-check-and-consume-no-toctou-race)
    - [Reverse-proxy edge: CORS, forwarded headers and HTTPS posture](#reverse-proxy-edge-cors-forwarded-headers-and-https-posture)
    - [Request rate limiting](#request-rate-limiting)
    - [Graceful shutdown and SignalR sticky sessions](#graceful-shutdown-and-signalr-sticky-sessions)
- [HTTP API and domain](#http-api-and-domain)
    - [Tenant model and HTTP API](#tenant-model-and-http-api)
    - [Current principal](#current-principal)
    - [Organization create and read](#organization-create-and-read)
    - [Workspace member invites (scoped tokens)](#workspace-member-invites-scoped-tokens)
    - [Member removal (revoking access)](#member-removal-revoking-access)
    - [Workspace archive (lifecycle end-state)](#workspace-archive-lifecycle-end-state)
    - [Session create and list](#session-create-and-list)
    - [Session lifecycle commands](#session-lifecycle-commands)
    - [Session cancel (lifecycle off-ramp)](#session-cancel-lifecycle-off-ramp)
    - [Participant presence events (join / leave)](#participant-presence-events-join--leave)
    - [Reveal command](#reveal-command)
    - [Hide (un-reveal) command](#hide-un-reveal-command)
    - [Scene and content lifecycle session events](#scene-and-content-lifecycle-session-events)
    - [Audit log](#audit-log)
    - [Participant visible feed](#participant-visible-feed)
    - [Scene content APIs](#scene-content-apis)
    - [Entity relationship removal](#entity-relationship-removal)
    - [Entity deletion](#entity-deletion)
    - [Content block deletion](#content-block-deletion)
    - [Scene deletion](#scene-deletion)
- [Realtime](#realtime)
    - [Realtime hub](#realtime-hub)
- [Assets, storage and background jobs](#assets-storage-and-background-jobs)
    - [Secret management and the configuration contract](#secret-management-and-the-configuration-contract)
    - [Asset metadata](#asset-metadata)
    - [Asset storage adapter](#asset-storage-adapter)
    - [Concrete S3-compatible storage adapter](#concrete-s3-compatible-storage-adapter)
    - [Asset upload intent](#asset-upload-intent)
    - [Asset signed download](#asset-signed-download)
    - [Asset linking](#asset-linking)
    - [Asset cleanup job](#asset-cleanup-job)
    - [Recap generation job](#recap-generation-job)
    - [Export processing job](#export-processing-job)
    - [Store notification reconciliation job](#store-notification-reconciliation-job)
    - [Worker liveness heartbeat](#worker-liveness-heartbeat)
    - [Asset deletion](#asset-deletion)
    - [Asset-link removal](#asset-link-removal)
    - [Template deletion](#template-deletion)
    - [Export jobs](#export-jobs)
    - [Export manifests](#export-manifests)
    - [Recaps](#recaps)
- [Entitlements, quotas and monetization](#entitlements-quotas-and-monetization)
    - [Entitlement and plan definitions](#entitlement-and-plan-definitions)
    - [Subject entitlements](#subject-entitlements)
    - [Quota definitions and quota status](#quota-definitions-and-quota-status)
    - [Quota enforcement on protected commands](#quota-enforcement-on-protected-commands)
    - [Ad eligibility](#ad-eligibility)
    - [Current-user entitlements](#current-user-entitlements)
    - [Mobile API path shape (the `/v1` gateway)](#mobile-api-path-shape-the-v1-gateway)
    - [Purchase provider abstraction](#purchase-provider-abstraction)
    - [Purchase transaction persistence and audit trail](#purchase-transaction-persistence-and-audit-trail)
    - [Apple transaction verification endpoint](#apple-transaction-verification-endpoint)
    - [Google purchase token verification endpoint](#google-purchase-token-verification-endpoint)
    - [Buyer linkage for verified purchases](#buyer-linkage-for-verified-purchases)
    - [Store notifications](#store-notifications)
- [Container images](#container-images)
    - [Publishing release images (CORE-OPS-009)](#publishing-release-images-core-ops-009)
    - [Supply chain: pinned base images, SBOM and CVE scan (CORE-DEP-003)](#supply-chain-pinned-base-images-sbom-and-cve-scan-core-dep-003)
    - [Backup and restore (CORE-OPS-010)](#backup-and-restore-core-ops-010)
- [Continuous integration](#continuous-integration)
- [License](#license)

## Quick start

> Prerequisites: .NET SDK 10, Node.js 22 and pnpm 10 (Docker optional). See
> [Prerequisites](#prerequisites) for details. Run every command from the
> repository root; CI runs them verbatim, so a green local run means a green
> pipeline.

```bash
# Clone
git clone https://github.com/Live-Experience-Application/livecore-platform.git
cd livecore-platform

# .NET solution: API host, background worker and tests
dotnet build LiveCore.slnx
dotnet test  LiveCore.slnx

# TypeScript packages: contracts, SDK, UI core and design tokens
pnpm install
pnpm --recursive run build
pnpm --recursive run test

# Run the hosts directly — each in its own terminal (API on http://localhost:5062)
dotnet run --project apps/api
dotnet run --project apps/worker
```

Prefer containers? Bring up **PostgreSQL + the migrations runner + the API + the
worker** with the in-repo deployment stack — from `deploy/compose`:

```bash
docker compose up -d --build
```

For configuration, the coverage/boundary/spec gates, the health and metrics
endpoints, migrations and the full runtime reference, continue with
[Build, format, lint, test and boundary scan](#build-format-lint-test-and-boundary-scan)
and [Run the hosts locally](#run-the-hosts-locally).

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
.github/workflows/ci.yml CI pipeline (build, tests, code-coverage report + gate, format/lint, boundary scan, image builds; on a release tag it produces an SBOM + CVE scan and pushes versioned images)
scripts/LiveCoreImageTags.psm1 release image tag derivation (immutable, versioned, fail-closed off a release tag)
scripts/derive-image-tags.ps1  CLI the publish job uses to derive the API/worker image references
scripts/test-image-tags.ps1    tests for the image tag derivation (immutable + versioned + fail-closed)
scripts/LiveCoreReleaseVersion.psm1 release-tag-vs-package-version gate logic: reads the four packages' shared version (fail-closed if they disagree) and compares it to the release tag's version (CORE-CMP-003)
scripts/assert-release-version.ps1  CLI the publish job runs to fail the publish when the release tag's version does not equal the packages' shared version (drift cannot ship, CORE-CMP-003)
scripts/test-release-version.ps1    tests for the release-version gate (a matching tag passes; a mismatching tag, a non-release ref, or packages out of lockstep fail closed, CORE-CMP-003)
scripts/LiveCoreImageScan.psm1 supply-chain publish-gate logic: the CVE-scan pass/fail decision and the SBOM validity check (CORE-DEP-003)
scripts/assert-image-scan.ps1  CLI the publish job runs to fail the publish on a critical vulnerability or a missing/empty SBOM (fail-closed; report-only in the dry-run)
scripts/test-image-scan.ps1    tests for the scan gate + SBOM check (a seeded critical CVE fails the gate, CORE-DEP-003)
scripts/LiveCoreCoverage.psm1  code-coverage gate logic: merges the Cobertura reports into one de-duplicated, production-focused line-coverage number and the threshold pass/fail decision (CORE-TST-001)
scripts/assert-coverage.ps1    CLI the CI coverage job runs to report coverage and fail below the minimum (fail-closed; report-only while the gate is non-blocking, CORE-TST-001)
scripts/test-coverage-gate.ps1 tests for the coverage gate (a deliberately-untested new handler trips the threshold once blocking is enabled, CORE-TST-001)
.dockerignore            build-context exclusions for the container image builds
eslint.config.mjs        ESLint flat config for the TypeScript packages
.prettierrc.json         Prettier configuration (with .prettierignore)
apps/api                 ASP.NET Core API host (LiveCore.Api) - health endpoints, IdentityAccess module
apps/api/Dockerfile      container image for the API host (multi-stage)
apps/api/Migrations.Dockerfile  one-shot migrations runner image (applies EF Core migrations before API rollout)
apps/worker              Background worker host (LiveCore.Worker) - runs the asset cleanup, recap generation and export processing jobs (and the billing-gated store-notification reconciliation job)
apps/worker/Dockerfile   container image for the worker host (multi-stage)
packages/contracts       @livecore/contracts  - TypeScript contract types (DTOs, enums, events)
packages/sdk-ts          @livecore/sdk-ts     - TypeScript SDK client (typed Core API client over @livecore/contracts)
packages/ui-core         @livecore/ui-core    - generic, framework-agnostic UI primitive contracts (variant vocabularies, prop shapes, variant defaults)
packages/design-tokens   @livecore/design-tokens - generic design tokens and theme contracts
tests/LiveCore.Api.UnitTests  xUnit unit tests for the API domain modules (IdentityAccess)
tests/LiveCore.SmokeTests  xUnit smoke and health endpoint tests for the hosts
tests/version-lockstep/version-lockstep.test.mjs  cross-package version lockstep test: the four packages share one version across package.json + exported VERSION + per-package + root CHANGELOG (CORE-CMP-003; run via `pnpm run test:versions`)
scripts/boundary-scan.ps1  forbidden-term boundary scan for Core source
scripts/spec-consistency.ps1  doc/csv/code route/table/event/epic consistency check CLI (CORE-DOC-001, CORE-SPEC-001)
scripts/LiveCoreSpecConsistency.psm1  spec-consistency check logic: name-set invariants + semantic checks against the minimal-API registrations and EF model snapshot (CORE-SPEC-001)
scripts/test-spec-consistency.ps1  seeded-drift tests for the spec-consistency checks (a changed role, an undocumented endpoint, an entitlement event with no audit action fail; the real tree passes — CORE-SPEC-001)
scripts/LiveCoreBackup.psm1  backup/restore coverage + integrity logic and the at-rest encryption sink for the systems of record (CORE-OPS-010, CORE-DR-001)
scripts/backup-livecore.ps1  backs up PostgreSQL + object storage, encrypts the dump and mirrored assets at rest, and writes a coverage manifest (fail-closed without an encryption passphrase)
scripts/restore-livecore.ps1 decrypts, restores PostgreSQL + object storage and verifies the systems of record
scripts/test-backup-restore-drill.ps1  runnable restore drill (round-trip + fail-closed checks, including the encryption sink)
scripts/test-backup-restore-postgres.ps1  real backup/restore round-trip against a live Postgres (CORE-DR-002 CI gate)
scripts/LiveCoreMigrationLint.psm1  destructive migration Down() detection + acknowledgement-baseline reconciliation (CORE-DR-004)
scripts/lint-migration-downs.ps1  CI lint that flags a Down() dropping a table/column for review (roll-forward-only policy, CORE-DR-004)
scripts/test-migration-down-lint.ps1  tests for the destructive-Down lint logic (CORE-DR-004)
scripts/LiveCoreComposeDeploy.psm1  compose deployment-manifest validation (migrate gate, postgres healthcheck, required services, documented probes; CORE-DEP-001)
scripts/test-compose-deploy.ps1  tests the compose validation and guards deploy/compose/docker-compose.yml (CORE-DEP-001)
deploy/compose           in-repo Docker Compose deployment stack: postgres + migrations runner + API + worker, with the migrate-before-API gate and documented health/readiness/liveness probes (CORE-DEP-001)
docs/                    architecture and product documentation
csv/                     backlog stories and forbidden term list
```

## Mobile-related Core extension

The Core includes product-neutral Entitlements, Quotas, Purchase Verification and Ad Eligibility contracts so that mobile apps cannot bypass limits or premium state client-side.
Core does not render ads, own mobile screens, or contain App Store / Google Play marketing copy.
Billing/monetization is in scope for Core v1 (`docs/01_PRODUCT_VISION_AND_SCOPE.md`): a verified purchase is persisted, auditable and grants the buyer the mapped `SubjectEntitlement`, refunds/cancellations revoke or downgrade it, and free-tier quotas are enforced server-side. The verify-and-record foundation (CORE-STORE-001..005, CORE-ENTL-001..004, CORE-ADS-001) is implemented, and so is the purchase-to-entitlement grant chain: the `billing_account_links` buyer linkage (CORE-MON-002), the product-to-plan-to-entitlement grant (CORE-MON-003 — a verified, buyer-linked purchase grants the mapped `SubjectEntitlement` idempotently, reusing the existing plan/assignment model with no new table), and the **monotonic purchase status machine** (CORE-MON-004 — the revoked states `Refunded`/`Cancelled` are terminal/absorbing, so a refund/cancellation/chargeback revokes the granted entitlement and **stays revoked**: a later renewal cannot resurrect it on the webhook or through reconciliation, which re-derives status by a monotonic fold over the recorded notifications). The free-tier participant cap is now enforced server-side too (CORE-MON-005 — `session.participant.max` is enforced on the participant-join path via the existing `QuotaEnforcementService`, keyed on a new `Session` quota subject so each session has its own atomic participant counter; a join is rejected once the session is at its plan limit, concurrent joins cannot overrun the cap (CORE-CONC-004), a paid plan admits more, and a leaving participant releases the slot — no new table, no migration). The free-tier **storage** cap is now enforced server-side too (CORE-MON-006 — `asset.storage.bytes.max` is enforced on the asset upload-intent path via the existing `QuotaEnforcementService`, keyed on the asset's `Workspace` quota subject so each workspace has its own atomic byte counter; the client declares the object size at upload-intent and an upload is rejected once it would take the workspace over its plan storage limit, concurrent uploads cannot overrun the cap (CORE-CONC-004), the consume + signed-URL mint + row persist run in one transaction so an over-quota 409 or an unconfigured-storage 503 leaves no orphan asset and no leaked quota, and the host-initiated asset deletion releases the reserved bytes so freeing an asset restores headroom — no new key in the catalog, no new table, no migration). The `workspace.active.max` quota now releases symmetrically on archive too (CORE-MON-007 — `ArchiveWorkspaceAsync` calls the existing `QuotaEnforcementService.ReleaseAsync` for the same `(User, workspace.active.max)` pair the create consumed, mirroring session start/end; without it the active-workspace list excludes the archived workspace while its usage stayed consumed, so a free user (limit 1) was locked out forever after a single create→archive. The release is a clamped, idempotent decrement that runs only after the authorized, persisted `Active→Archived` transition, so a denied archive frees nothing and a double-archive — rejected `409` by the terminal `CanArchive` guard before any release — never double-releases; no new table, no migration). The **receipt-verification adapter contract** is now explicit for sandbox/production separation and replay protection (CORE-MON-008 — Apple/Google receipt verification stays delegated to a deployment-supplied adapter behind the fail-closed `IPurchaseVerificationProvider` port that ships no provider keys; a `VerifiedPurchase` now carries the verified `PurchaseEnvironment` and the fail-closed `PurchaseEnvironmentPolicy` makes a **production** deployment honor only a `Production` purchase and reject a `Sandbox` one — so "a sandbox receipt is not honored in production": it is `422` and records/grants nothing, decided **before** any persistence or grant. Receipt-replay defence is two-layered: the adapter rejects an already-consumed proof (`422`), and recording stays idempotent on `(provider, provider_transaction_id)` so a replayed-but-genuine proof grants nothing twice. The cryptographic verification itself is adapter-supplied, out of Core per threat T7/`docs/13`; no new table, route or migration — the environment is consumed by the honoring gate before a purchase is recorded). The mobile-facing `/v1` route shape now resolves in-process (CORE-MON-009 — the documented mobile store/entitlement paths in `csv/mobile_store_api_routes.csv` are served under the same host as the `/api/v1` endpoints they map to, so a mobile client following the CSV literally reaches the endpoint instead of `404`-ing; authorization and tenant scoping are unchanged and stay server-side). The Monetization v1 epic is now complete: **store-notification handling is atomic** (CORE-MON-010 — applying a store notification's purchase **status change** and writing its **dedup-ledger row** happen in **one transaction** by reusing the CORE-CONC-002 `TransactionalUnitOfWork`; previously the status change committed first and only then the `store_notification_events` row was inserted, in separate transactions, so a crash between them left the status applied but the notification unrecorded and a re-delivery re-applied it — double-appending the `purchase_events` audit trail. Now a part-way failure rolls **everything** back (the entitlement revocation for a revoking notification included), so a re-delivery either dedups on the present ledger row or replays the whole effect from scratch — never a status applied without its first-arrival record, never a duplicated audit entry. The dedup fast-path read stays outside the transaction; the unique `store_notification_events(provider, provider_notification_id)` index remains the race guard. The reconciliation job writes no ledger row, so it needs no such wrap; a SQL window-function candidate query for the in-memory latest-per-purchase scan stays a documented follow-up, fine for the off-by-default low-volume job — no new table, route or migration). The epic reversed the earlier CORE-DOC-002 post-v1 deferral. The single source of truth for the v1 monetization scope and acceptance is `docs/24_SPEC_CONSISTENCY.md`.

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

### Code coverage and the coverage gate

Collect coverage with the referenced `coverlet.collector` and run the threshold
gate (CORE-TST-001). The CI `coverage` job runs the same commands:

```bash
dotnet test LiveCore.slnx --collect:"XPlat Code Coverage" --results-directory ./TestResults
pwsh -NoProfile -File scripts/assert-coverage.ps1 -ReportDirectory ./TestResults -MinimumLineCoverage 80 -ReportOnly
```

`assert-coverage.ps1` merges the per-project Cobertura reports into one
de-duplicated, production-focused line-coverage number (test assemblies and
generated EF migrations excluded) and checks it against the minimum. The gate is
**non-blocking** for now (`-ReportOnly` reports and warns without failing); drop
`-ReportOnly` to make a regression below the minimum fail the build. The gate
logic is tested by `scripts/test-coverage-gate.ps1`. See
`docs/14_TESTING_STRATEGY.md` ("Coverage measurement and the CI gate") and
`docs/17_DEFINITION_OF_DONE.md`.

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

The **lockstep** itself is enforced too (CORE-CMP-003). The per-package tests
above each check only their **own** version triple, so a cross-package test
(`tests/version-lockstep/version-lockstep.test.mjs`, run by `pnpm run test:versions`
and the CI `typescript` job) asserts the four packages share **one** version
across `package.json`, the exported `VERSION` and every `CHANGELOG.md` (including
the root `CHANGELOG.md`) — so bumping one package out of lockstep fails CI. And
because the API/worker host images are versioned by the **release tag** rather
than a manifest of their own, a publish gate (`scripts/assert-release-version.ps1`,
run by the CI `publish` job before any image is built or pushed) asserts the
release tag's version equals the packages' shared version, so a tag that drifts
from the package version cannot ship. See `docs/23_PACKAGE_VERSIONING.md`.

### Boundary scan

Run the boundary scan (fails with a non-zero exit code if any forbidden
vertical or brand/platform term from `csv/forbidden_core_terms.csv` appears in
Core source). It enumerates **tracked** files only (`git ls-files`), so
gitignored local tooling is never scanned; it covers every tracked text source
including Dockerfiles (`apps/*/Dockerfile`, `*.Dockerfile`); and it excludes only
the documentation that legitimately lists the terms — the `docs/` and `csv/`
trees and the root files `README.md`, `AGENTS.md`, `LICENSE` and `CHANGELOG.md`.
It is fail-closed: with no git work tree to enumerate it exits with code `2`
rather than passing.

```powershell
# Windows (Windows PowerShell 5.1 or pwsh)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/boundary-scan.ps1
```

```bash
# Linux/macOS (PowerShell 7+)
pwsh -NoProfile -File scripts/boundary-scan.ps1
```

Its logic is unit-tested over a throwaway git work tree by
`scripts/test-boundary-scan.ps1` (run in CI before the scan itself).

### Spec consistency check

Run the spec consistency check (CORE-DOC-001, extended by CORE-SPEC-001 and
CORE-SPEC-002). It fails with a non-zero exit code when the route, table, event or
epic specifications in `docs/` and `csv/` drift from each other, from their single
source of truth (the source-of-truth map is `docs/24_SPEC_CONSISTENCY.md`) **or
from the implementation** — it now also validates `csv/api_routes.csv` against
the routes the minimal-API registrations mount (both directions), the documented
roles/auth, the entitlement event catalog (binding each `audit=true` event to a
real `AuditAction` enum member, CORE-SPEC-002), the mobile store CSV against the
in-process gateway route table, and `csv/database_tables.csv` plus its promised
unique indexes against the EF Core model snapshot. CI runs it as the
`spec-consistency` job (which first runs `scripts/test-spec-consistency.ps1`,
the seeded-drift tests for the check logic in
`scripts/LiveCoreSpecConsistency.psm1`).

```powershell
# Windows (Windows PowerShell 5.1 or pwsh)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/spec-consistency.ps1
```

```bash
# Linux/macOS (PowerShell 7+)
pwsh -NoProfile -File scripts/spec-consistency.ps1
```

## Run the hosts locally

Start the API host (listens on `http://localhost:5062` by default, see
`apps/api/Properties/launchSettings.json`):

```bash
dotnet run --project apps/api
```

Start the background worker host (runs the asset cleanup, recap generation and
export processing jobs when a database is configured, plus the billing-gated
store-notification reconciliation job when it is enabled; see "Asset cleanup job",
"Recap generation job", "Export processing job" and "Store notification
reconciliation job" below):

```bash
dotnet run --project apps/worker
```

### Deploy the whole stack with Docker Compose

The repository ships a runnable deployment stack at
[`deploy/compose/docker-compose.yml`](deploy/compose/docker-compose.yml)
(CORE-DEP-001), so an operator can deploy **PostgreSQL + the migrations runner +
the API + the worker** from this repository alone. From `deploy/compose`:

```bash
docker compose up -d --build
```

Compose builds the images from the in-repo Dockerfiles and starts the services in
order: `postgres` becomes healthy, the one-shot `migrate` runner applies the schema
and exits, and only then do `api` and `worker` start — the **migrate-before-API
gate**, expressed as `depends_on: { migrate: { condition:
service_completed_successfully } }`. It reuses the documented env contract
(`.env.example`), comes up green for local use with no extra setup, and documents
production hardening in [`deploy/compose/README.md`](deploy/compose/README.md). The
migrate gate and the documented health/readiness/liveness probes are tested by
`scripts/test-compose-deploy.ps1` and the `compose-smoke` CI job. See
`docs/13_SELF_HOSTING_REQUIREMENTS.md` ("In-repo deployment manifest").

## Operations and observability

Observe and operate a running host: the health and metrics endpoints, the AGPL
source-offer endpoint, worker metrics, structured logging and distributed tracing.

### Health endpoints

The API host exposes two unauthenticated health endpoints:

| Endpoint        | Purpose                                                                                                                                                                 |
| --------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `/health/live`  | Liveness: the process is up and serving HTTP. Runs no dependency checks on purpose.                                                                                     |
| `/health/ready` | Readiness: runs the health checks tagged `ready` (the `database` connectivity check, registered only when a connection string is configured, plus the production gate). |

Both return `200 OK` with the minimal JSON body `{"status":"Healthy"}`;
readiness returns `503` with `{"status":"Unhealthy"}` once a registered
readiness check fails. Because the endpoints are reachable without
authentication, the response carries only the overall status: no version
numbers, configuration values, host names or individual check details (see
`docs/07_SECURITY_THREAT_MODEL.md`).

In a **Production** environment readiness additionally fails when a required
dependency is unconfigured (CORE-OPS-005): persistence (`ConnectionStrings:Database`)
or OIDC (`Authentication:Oidc:Authority`). Previously `/health/ready` could report
`Healthy` with no database configured, even though every domain route then fails
closed with `503` — so orchestration would route live traffic at an API that cannot
serve it. The production required-dependency gate reports not-ready in that case so a
misconfigured production host leaves the ready rotation, while `/health/live` stays
`200` (a not-ready misconfiguration must never trigger a restart of an otherwise live
process). Outside `Production` the gate is inert, preserving the local-development
latitude of running without a database or an identity provider (the same
environment-aware posture as the OIDC audience guard, CORE-OPS-004). The response
stays status-only, so which dependency is missing never leaks to the unauthenticated
endpoint.

### Metrics endpoint

The API host exposes operational metrics on a Prometheus scrape endpoint
(CORE-OBS-001):

| Endpoint   | Purpose                                                                                      |
| ---------- | -------------------------------------------------------------------------------------------- |
| `/metrics` | OpenTelemetry-collected operational metrics in the Prometheus exposition format (scrape it). |

`docs/15_OBSERVABILITY.md` mandates eight operational signals; they are
implemented with OpenTelemetry over the vendor-neutral `System.Diagnostics.Metrics`
API. A single owner, `LiveCoreMetrics`, defines one `LiveCore` meter carrying all
eight instruments — **API request duration** and **error rate** (a request
middleware), **realtime connections** (the SignalR hub), **reveal command latency**
(the reveal endpoint), **event-delivery failures** (the session-event publisher),
**asset upload/download failures** (a transparent `IAssetStorage` decorator),
**database query failures** (an EF Core command interceptor) and **background job
failures** (the worker's cleanup job). The OpenTelemetry SDK aggregates them and the
Prometheus exporter serves `/metrics`.

Like `/health/*`, `/metrics` is **unauthenticated by convention** — a Prometheus
server scrapes it from inside the deployment network — and a deployment restricts it
at the reverse-proxy/network edge. It carries only low-cardinality aggregate series
(method, route **template**, status code, a coarse operation/job name); no tenant
identifier, token, asset coordinate or resource content is ever a metric label, so
the surface cannot leak content (threat T7 in `docs/07_SECURITY_THREAT_MODEL.md`).
The error counter counts only server errors (5xx); the fail-closed `401`/`403`/`404`
the authorization model returns by design are not counted as errors. Two new
dependencies are added to `apps/api`: `OpenTelemetry.Extensions.Hosting` (the SDK +
host integration) and `OpenTelemetry.Exporter.Prometheus.AspNetCore` (the scrape
endpoint). The Prometheus exporter is pinned to a **prerelease** (`1.16.0-beta.1`)
because the OpenTelemetry .NET Prometheus exporter has no stable release; that pin is
explicitly justified per `AGENTS.md` and its supply-chain risk is contained by the
locked-mode restore (enforced in CI and in every image build) and the publish-time
CVE scan — see `docs/15_OBSERVABILITY.md` and `docs/13_SELF_HOSTING_REQUIREMENTS.md`
(CORE-CMP-002). The background worker records job failures onto the same meter and now
exposes its **own** Prometheus scrape endpoint too (see "Worker metrics and per-loop
liveness" below).

### Source offer endpoint (AGPL section 13)

Because the Core is AGPL-3.0-or-later and network-interactive (the SignalR hub plus
the `/api/v1` surface), AGPL-3.0 section 13 obliges a hosted deployment to offer
remote users access to its Corresponding Source. The API host exposes one
unauthenticated endpoint that discharges that obligation (CORE-CMP-001):

| Endpoint      | Purpose                                                                                                         |
| ------------- | --------------------------------------------------------------------------------------------------------------- |
| `GET /source` | The AGPL section 13 source offer: the SPDX license, the running build version and where the source is obtained. |

It returns JSON — `{ "license", "version", "sourceUrl" }` — and requires no
authentication, because the offer is owed to every remote user the application
interacts with. The build version is read from the running assembly, so the offer
always identifies the exact source revision deployed. A deployment that runs
**modified** source must offer **its own** Corresponding Source, so the offered
location is configuration-overridable with `SourceOffer:RepositoryUrl`
(`SourceOffer__RepositoryUrl`); unset, it falls back to the canonical upstream
repository.

Like `/health/*` and `/metrics`, `/source` is a top-level infrastructure route, not
part of the versioned `/api/v1` product surface, and it exposes only the license, a
build version and a public repository URL — never a token, tenant identifier,
configuration value or resource content (threat T7 in
`docs/07_SECURITY_THREAT_MODEL.md`; `docs/16_LICENSING.md`).

### Worker metrics and per-loop liveness

The background worker is the host doing **irreversible** work, so it must not be a
monitoring blind spot (CORE-DR-003). It now serves a small HTTP surface — reusing the
ASP.NET Core shared framework the referenced API project already brings, so **no new
dependency** — bound to a configurable listen URL (`Worker:Metrics:Url`, default
`http://0.0.0.0:9464`):

| Endpoint       | Purpose                                                                                                                                                                                                                                                                        |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `/metrics`     | The Prometheus scrape endpoint, wired exactly as the API's (`AddLiveCorePrometheusMetrics`). It exposes the same `LiveCore` series, so the `livecore_job_failures_total` counter each loop records on failure is scrapeable (it was recorded onto an unobserved meter before). |
| `/health/live` | The **per-loop** liveness endpoint: healthy only when **every** active job loop is beating.                                                                                                                                                                                    |

Each of the four loops (asset cleanup, recap generation, export processing, and the
billing-gated store-notification reconciliation) writes the current UTC timestamp to
its **own** heartbeat file each tick; `/health/live` is healthy only when every active
loop's file is fresh (within `Worker:Heartbeat:StaleAfter`, default 2 hours). Before
this, all loops shared **one** file, so a single healthy loop kept it fresh and
**masked** the others hanging; per-loop files plus the aggregating endpoint make a
**single** hung loop detectable, and orchestration restarts the wedged worker. Like the
API's `/metrics` and `/health/*`, both worker endpoints are **unauthenticated by
convention** and restricted at the network edge, carrying only low-cardinality
aggregates and an overall status — never content (threat T7). See
`docs/13_SELF_HOSTING_REQUIREMENTS.md` and `docs/15_OBSERVABILITY.md`.

### Structured logging

Both hosts write structured, single-line JSON log entries to stdout using the
JSON console formatter built into `Microsoft.Extensions.Logging` (UTC
timestamps, scopes included); no external logging dependency is used. Log
levels are configured per host in `appsettings.json`. Logs must carry
identifiers and metadata, never sensitive content (threat T7 in
`docs/07_SECURITY_THREAT_MODEL.md`).

`docs/15_OBSERVABILITY.md` requires a documented per-request context on every
request/event log line (`request_id`, `organization_id`, `workspace_id`,
`session_id`, `user_id`, `event_id`). CORE-OBS-002 populates it. A single
request-scoped owner, `RequestLogContext` (`apps/api/Observability/`), holds
those identifiers under their documented snake_case keys, and the
`RequestLogContextMiddleware` opens **one** logging scope around the request with
it as the scope state — so the JSON formatter renders the populated identifiers
on every log line the request emits, plus a request-summary line the middleware
logs at completion. The middleware runs after authentication and before
authorization, seeding `request_id` (always), `user_id` (the authenticated
principal's OIDC subject) and the route-derived `workspace_id`/`session_id`
(surrogate `Guid`s only); the authoritative owners enrich the rest in the same
scope — `TenantContextResolver` sets `organization_id` from the resolved tenant
(only on success), and `SessionEventPublisher` sets `event_id` from the published
event. It is fail-safe (an anonymous caller carries no `user_id`, a denied tenant
logs no `organization_id`) and skips the unauthenticated `/health/*` and
`/metrics` endpoints. Only identifiers and authorization metadata are ever
logged — never the access token, the display name, the email or resource content
(threat T7).

### Distributed tracing

`docs/15_OBSERVABILITY.md` defers trace propagation to "later, when multiple
services are deployed"; CORE-OBS-003 lands the tracing hooks ahead of that so the
seams exist. A single owner, `LiveCoreActivitySource` (`apps/api/Observability/`),
defines one `LiveCore` `ActivitySource`, and the **key request and realtime
flows** produce spans on it: the HTTP request pipeline (`RequestTracingMiddleware`,
a `http.server.request` Server span), the reveal/hide command (`livecore.reveal`)
and the session-event publish (`livecore.session_event.publish`). The request span
is opened at the top of the pipeline so it wraps the whole request, so a reveal
produces one trace shaped `request → reveal → publish` (each durable event a
publish child of the reveal span) that a collector reconstructs into the request's
span tree.

The spans are exported with the OpenTelemetry SDK + host integration already
present for the metrics; one new dependency is added to `apps/api`:
`OpenTelemetry.Exporter.OpenTelemetryProtocol`, the **OTLP** trace exporter —
OpenTelemetry's vendor-neutral export protocol that every major collector ingests
(the OpenTelemetry Collector, Jaeger, Tempo, vendor backends); reimplementing span
batching and the OTLP wire format by hand would duplicate a correctness-sensitive
subsystem. It is wired **only when a collector endpoint is configured**
(`Tracing:Otlp:Endpoint`): unconfigured, spans are still produced (and any
in-process listener observes them) but shipped nowhere, so the host never reaches a
non-existent collector — the same fail-closed/inert posture as the storage adapter
and realtime backplane. The endpoint is read from configuration only; none lives in
source. Every span carries only low-cardinality, non-sensitive attributes — the
HTTP method, the route **template** (never the concrete path), the status code, a
coarse operation name and the stable session-event type name — never a token,
tenant identifier or resource content (threat T7), and the `/health/*` and
`/metrics` infrastructure endpoints are not traced.

## Identity, persistence and migrations

The host's authentication model (the OIDC principal), the persisted user-profile
reference, and how database migrations are applied as a deployment step.

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

CI additionally hardens the migrations against model drift and SQLite-only test
coverage (CORE-OPS-002): the `integration-postgres` job runs the full integration
suite against a real PostgreSQL service container — where each test's schema is
applied by the **real migrations** (`Database.Migrate()`), not the SQLite
`EnsureCreated()` schema — and runs `dotnet ef migrations has-pending-model-changes`
as a model-vs-migration drift gate. The integration suite is provider-switchable
through the `LIVECORE_TEST_DB_PROVIDER`/`LIVECORE_TEST_POSTGRES` environment
variables; with them unset it stays on in-memory SQLite, so local runs need no
database server.

The same `integration-postgres` job also runs the **cross-instance realtime
backplane propagation test** (CORE-TST-003): it adds a Redis/Valkey service
container and a `LIVECORE_TEST_REDIS` connection string, so
`RedisBackplanePropagationTests` boots multiple API instances sharing one
PostgreSQL system of record and the **real** Redis/Valkey SignalR backplane
(CORE-OPS-007) and proves an event published on one instance reaches a client
connected to another instance, while a different `ChannelPrefix` does not leak.
With `LIVECORE_TEST_REDIS` unset (a default `dotnet test`) the test is skipped, so
no Redis/Valkey server is needed locally. No credentials live in the repository:
the connection string points at the ephemeral CI service container only (threat
T7).

The **unit and smoke suites** now also run against real PostgreSQL
(CORE-TST-004). The `.NET` `dotnet` job's whole-solution test step runs the unit +
smoke + integration projects on in-memory SQLite (that step used to be mislabeled
"Run smoke tests" although it runs the whole solution; it is now named to match
what it runs). Because the unit/repository tests construct SQLite directly, their
**provider-specific** behavior — collation, case-sensitivity, JSON and the
Npgsql-only `xmin` optimistic-concurrency token — was never exercised at the unit
level on real PostgreSQL; only the separate `integration-postgres` job used real
PostgreSQL, and only for the integration project. The new `unit-smoke-postgres` job
runs the **unit and smoke** suites against a real PostgreSQL service with
`LIVECORE_TEST_DB_PROVIDER=Postgres` set, so a provider-divergent repository test
(`ProviderDivergentConcurrencyTests`) runs on real PostgreSQL and proves the
cross-context concurrency conflict the SQLite path cannot — it passes on **both**
providers (a `DbUpdateConcurrencyException` on PostgreSQL, last-write-wins on
SQLite). Such tests opt into the real database through the unit suite's
`ProviderTestDatabase` helper, which provisions a throwaway, migration-schema
PostgreSQL database when the environment selects PostgreSQL and falls back to
in-memory SQLite otherwise — so a default `dotnet test` still needs no database
server, and no credential is committed (the connection string points at the
ephemeral CI service container only, threat T7). See
`docs/14_TESTING_STRATEGY.md` ("Which provider the tests run against").

Migrations are **roll-forward-only** (CORE-DR-004): the runner applies `Up()`
only and a `Down()` is never run in production, because every `Down()` is
destructive (it drops the table/column its `Up()` added). The backward path for a
bad deploy is to roll the **application image** back — made safe by the
**expand/contract** discipline for schema changes — and to **restore from backup**
only when data was actually lost. The full policy, runbook and expand/contract
guidance are in `docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Migration rollback
policy"). A CI lint (the `migration-down-lint` job, `scripts/lint-migration-downs.ps1`)
flags any migration whose `Down()` drops a table or column so it cannot merge
without being reviewed and acknowledged in `csv/migration_destructive_down_review.csv`.

## Runtime resilience and edge

The correctness and resilience guarantees behind every command — optimistic
concurrency, transactional commit-then-publish, database retry on transient
failures, worker concurrency handling and atomic quota check-and-consume — plus
the reverse-proxy edge posture, request rate limiting and graceful shutdown.

### Optimistic concurrency

The mutable aggregates carry an optimistic-concurrency token so a concurrent
read-modify-write fails loudly instead of silently losing an update. CORE-CONC-001
covered the first six (`Session`, `VisibilityRule`, `Workspace`, `Participant`,
`PurchaseTransaction` and quota usage); CORE-CONC-006 extended the token to every other
in-place-updated aggregate (`ContentBlock`, `Entity`, `EntityType`, `Scene`, `Asset`,
`SubjectEntitlement`, `ExportJob` and the `UserProfile` reference), which had still been
doing a bare `Update`/`SaveChanges` and so silently lost concurrent updates. The token is
the PostgreSQL system column `xmin`, mapped as an EF Core
row-version concurrency token, so PostgreSQL bumps it on every UPDATE and EF appends
`WHERE ... AND xmin = @original` to a write. When two commands interleave on one row —
for example a session `start` racing a session `end`, a reveal racing a hide, or two
`Scene.Reorder` writes racing — the
second writer's stale write is rejected with a `DbUpdateConcurrencyException`, which the
`ConcurrencyConflictMiddleware` translates into a fail-closed `409 Conflict` (reload and
retry) rather than overwriting the first writer's change. The in-memory state-machine
guards (`Session.CanStart`/`CanEnd`, `VisibilityRule.ChangeVisibility`) run per
`DbContext` and give no cross-context/replica protection, so the row-version token is the
cross-context guarantee.

`xmin` is a system column every PostgreSQL row already carries, so the token needs **no
data migration** and adds no real column (the `AddOptimisticConcurrencyTokens` and
`AddOptimisticConcurrencyTokensToRemainingAggregates` migrations are deliberate
schema-level no-ops; the token lives in the EF model only). Because `xmin`
is PostgreSQL-specific, the mapping is applied only on the Npgsql provider — the default
in-memory SQLite test provider has no such column and is left untouched; the real
cross-context conflict is exercised by the integration suite's PostgreSQL job. See
`docs/10_DATABASE_SCHEMA.md` and `docs/08_API_CONTRACTS.md`.

### Transactional unit of work (commit-then-publish)

A command that changes a rule/state row, appends an audit fact, appends a durable
session event and changes a quota counter now commits all of those in **one database
transaction** (CORE-CONC-002). Previously each repository call was its own
`SaveChangesAsync`, so a crash between steps could leave visibility changed with no
`ContentRevealed` event (replay would reconstruct a state the append-only stream never
recorded) or, on a retry, a double audit/event. The reveal/hide and session
start/end/cancel endpoints wrap their writes in `TransactionalUnitOfWork`, so a part-way
failure rolls **everything** back and the append-only event stream can never diverge from
persisted state — there is never an orphan audit record or a half-applied reveal.

The transaction is opened **inside** the EF Core execution strategy
(`Database.CreateExecutionStrategy().ExecuteAsync(async () => { BeginTransactionAsync …
CommitAsync })`), never a bare user-initiated `BeginTransaction`. That is the form a
retrying execution strategy requires (it must be able to re-run the whole unit of work
after a transient failure), so the commands stay correct once CORE-CONC-003 enables
`EnableRetryOnFailure`; it is equally correct under today's default non-retrying strategy.
Every repository writes through the same scoped `LiveCoreDbContext`, so each
`SaveChangesAsync` enrols in the one transaction.

Realtime delivery is **commit-then-publish**: the durable event is _appended_ inside the
transaction (`ISessionEventPublisher.AppendAsync`) but _delivered_ to its server-computed
recipients only **after** the commit (`ISessionEventPublisher.DeliverAsync`), so a
delivery failure can never roll back already-committed state (a reconnecting client
replays a missed push later). See `docs/10_DATABASE_SCHEMA.md` and
`docs/11_REALTIME_SYNC.md`.

### Database connection resilience (retry on transient failures)

Every `LiveCoreDbContext` the platform builds uses a **retrying execution strategy**
so a routine, **transient** PostgreSQL disruption is retried automatically by EF Core /
Npgsql instead of surfacing as a user-facing `5xx` or a worker-job exception
(CORE-CONC-003). In the documented topology (the API behind a proxy, PostgreSQL as a
**separate** service, `docs/02_ARCHITECTURE.md` / `docs/13_SELF_HOSTING_REQUIREMENTS.md`)
the disruptions the epic names — a failover/primary promotion, a database restart, a
brief network partition, momentary pool exhaustion — are expected and short-lived; a
retrying strategy re-runs the failed operation a few times with exponential back-off, so
an operation that succeeds on a retry never reaches the caller as an error. A
**non-transient** failure (a constraint violation, a query bug) is not retried and still
fails immediately, so resilience never masks a real error.

A single owner, `LiveCoreNpgsqlOptions.Configure`, turns retry on (`EnableRetryOnFailure`)
in one place and is passed to **every** `UseNpgsql` call — the API host
(`Program.cs`), each worker job context (asset cleanup, recap generation, export
processing, store-notification reconciliation) and the design-time/migrations factory —
so the API and every worker job share one resilience policy and migrations applied
against a separate database tolerate a transient blip. It is applied wherever `UseNpgsql`
is called, each of which is already **gated on a configured connection string**, so the
host still runs without persistence (fail-closed) exactly as before, and it reads no
configuration and holds no secret (threat T7).

Enabling retry is **safe** because every multi-step write already runs inside the EF
execution strategy's `ExecuteAsync` (the commit-then-publish unit of work above, plus the
resource-deletion commands), never a bare user-initiated `BeginTransaction` — a retrying
strategy rejects the latter because it could not re-run the work after a transient
failure. See `docs/02_ARCHITECTURE.md`.

When the strategy actually **retries**, the unit of work first **clears the EF change
tracker** before the re-run (CORE-CONC-005). A retry re-runs the whole delegate, but the
failed attempt's transaction has only rolled back in the **database** — its tracked
in-memory mutations (a `Prepared`→`Live` session transition, a flipped visibility rule, an
added idempotency-key/audit/event row) survive on the shared `LiveCoreDbContext`. Left in
place they would make the retry act on **stale** state: re-running `session.Start` on an
already-`Live` tracked entity throws `InvalidSessionStateTransitionException`, and a
re-added entity double-adds — turning a retryable blip into a hard `5xx`. `ChangeTracker.Clear()`
before the retry detaches them, so the retried work reloads the rolled-back database state;
each command therefore reads its entities **inside** the delegate (the reveal command, and
the session start/end/cancel command which now reloads the session inside its unit of work),
so a retried command runs against fresh state and succeeds. The **first** attempt is left
untouched, so the default non-retrying path is unchanged. This was latent until now because
the test suites used a plain non-retrying provider, so the delegate was never re-run; a
focused unit test and the `CommandRetryResilienceTests` HTTP tests now enable a retrying
strategy and inject a transient failure mid-delegate to pin the behavior.

### Concurrency conflicts in the worker job contexts

A `DbUpdateConcurrencyException` is no longer an **unhandled** worker-job exception
(CORE-CONC-007). The four worker loops share the same token-bearing model as the API, but
only the HTTP `ConcurrencyConflictMiddleware` turns that exception into a `409` — a worker
has no such middleware. The concrete case is the off-by-default store-notification
reconciliation sweep: it converges each drifted purchase with a tracked read-modify-write
on `PurchaseTransaction` (`ChangeStatusAsync`), so a purchase a webhook changes
concurrently makes that write **lose** the `xmin` race. The per-purchase loop in
`StoreNotificationReconciliationService` now catches the `DbUpdateConcurrencyException`
**explicitly** (distinct from a generic persistence error), logs it (provider and counts
only — never the transaction id or any content, threat T7), counts it as a failed
convergence and **skips-and-continues** — the conflicted purchase stays drifted for the
next sweep to retry, and the sweep is never torn down. Crucially, `PurchaseTransactionRepository.UpdateAsync`
now **detaches** the conflicted row after a lost race (the same "keep the context usable"
cleanup `AddAsync` already did after a failed insert) before rethrowing, so the abandoned,
still-`Modified` entity is not re-sent by a later `SaveChanges` on the **one** scoped
`DbContext` the sweep reuses across the whole batch — a single conflict no longer poisons
every subsequent purchase in the run. The rethrow keeps the HTTP path's `409` intact. A
worker unit test injects a simulated conflict on one reconciled purchase and asserts the
sweep skips it, still reconciles the rest of the batch on the same context, and converges
the skipped purchase on a later sweep; a repository test pins the genuine detach-and-rethrow.

### Atomic quota check-and-consume (no TOCTOU race)

A protected command that consumes a server-side quota does its check **and** its consume in
**one atomic, limit-guarded statement** (CORE-CONC-004). Previously `QuotaEnforcementService`
ran a `CheckAsync` read and a `RecordConsumptionAsync` write as two separate, non-transactional
steps with no row lock or reserved increment, so two concurrent `session/start` or
`workspace/create` commands could **both** read `used < limit` and **both** record — overrunning
`session.active.max` / `workspace.active.max`. `QuotaEnforcementService.TryConsumeAsync` replaces
that with a single conditional increment in the database
(`UPDATE quota_usage SET used_amount = used_amount + @amount WHERE … AND used_amount + @amount <= @limit`):
the cap is re-evaluated against the row the database locks, so a concurrent writer is rejected
rather than overrunning it. The result is that **N concurrent commands consuming the same quota at
a limit of one yield exactly one success and N-1 quota-exceeded** — the cap can never be exceeded
under a race. The first consumption inserts the usage row (only when the amount fits the cap); the
unique per-subject index makes a concurrent insert a safe lost-create race that re-runs the guarded
increment. An unlimited (fair-use) grant increments unconditionally; an ungoverned command (no
active quota definition) consumes nothing.

It is fail-closed and consistent with the status read: a subject not entitled to a defined quota
has **no** allowance (consumes nothing, denied), and the reported decision reuses the same
`QuotaStatus.Calculate` math the `GET /api/v1/.../quota-status` endpoints use. `workspace/create`
**reserves** the slot atomically before creating and releases it again if the create then fails
(a duplicate slug), so a rejected create never burns the user's allowance; `session/start` runs the
atomic consume **inside** the command's existing transaction (the commit-then-publish unit of work),
so an over-quota start commits nothing and the session stays `Prepared`. The schema is unchanged —
the consume reuses the existing Entitlements `quota_usage` table. A read-only `CheckAsync` remains
for the advisory pre-flight on a command that does **not** itself consume (creating a `Prepared`
session is blocked while the workspace already runs its maximum number of **live** sessions, which
are consumed at start). Releasing a counted resource (`session/end`) stays a clamped decrement.
See `docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`.

### Reverse-proxy edge: CORS, forwarded headers and HTTPS posture

The API is meant to run **behind a TLS-terminating reverse proxy** (CORE-OPS-003).
Three runtime settings make that posture correct and safe, all **fail-closed by
default** (the full deployment guide is in `docs/13_SELF_HOSTING_REQUIREMENTS.md`):

- **CORS allow-list (`Cors:AllowedOrigins`).** A browser/PWA front-end on a
  different origin may call the REST API **and** the `/hubs` SignalR endpoint only
  from an origin on this configured allow-list (one named policy applied to both,
  with credentials, because the list is always explicit origins — never a
  wildcard). The default is fail-closed: with no configured origins **no**
  cross-origin browser client is allowed (a disallowed origin's preflight gets no
  `Access-Control-Allow-Origin` header). Configure it as a list, e.g.
  `Cors__AllowedOrigins__0=https://app.example.com`. CORS is a browser-enforced
  boundary layered on the OIDC/tenant checks every endpoint already applies — it
  never widens server-side authorization (`docs/07_SECURITY_THREAT_MODEL.md`).
- **Forwarded headers (`ForwardedHeaders:KnownProxies` / `:KnownNetworks`).**
  `UseForwardedHeaders` restores the real client scheme/host/IP from the proxy's
  `X-Forwarded-Proto`/`-Host`/`-For` headers, but only when the immediate peer is a
  **trusted** proxy: loopback by default, plus any proxy IP / CIDR network named in
  configuration (e.g. `ForwardedHeaders__KnownNetworks__0=10.0.0.0/8`). With
  nothing configured only loopback is trusted, so an untrusted client cannot spoof
  `X-Forwarded-Proto: https` (threat T7). TLS is terminated at the proxy; the app
  adds no HTTPS redirect/HSTS of its own (that boundary lives at the edge).
- **Constrained host header (`AllowedHosts`).** No longer `*` — the default permits
  only `localhost;127.0.0.1`, and a deployment sets its real public host(s) (e.g.
  `AllowedHosts=app.example.com`) so requests with an unexpected `Host` are
  rejected.

### Request rate limiting

The API applies ASP.NET Core's built-in rate limiting (`UseRateLimiter`, CORE-SEC-001,
the "Security Hardening" epic). Before this the HTTP pipeline applied no rate limiting
anywhere, leaving two abuse/DoS surfaces unbounded: the two `AllowAnonymous`
store-notification webhooks (`POST /api/v1/store-notifications/{apple,google/rtdn}`),
which do database work and run a deployment-supplied external parser per call — a clear
DoS and ledger-amplification surface anyone can POST to, plus an invite-token /
`organizationSlug` enumeration surface — and every authenticated endpoint, which had no
per-caller ceiling. Two complementary fixed-window limiters close that, both built on the
shared framework (`Microsoft.AspNetCore.RateLimiting` / `System.Threading.RateLimiting`),
so **no new dependency** is added:

- A **strict per-IP** limit on the anonymous webhooks (the named
  `RateLimitingConfiguration.WebhookPolicyName` policy the webhook route group opts into
  with `RequireRateLimiting`). The partition key is the **real client IP** that
  `UseForwardedHeaders` (which runs first in the pipeline) restores from a trusted proxy,
  so the limit follows the actual caller, not the proxy hop (threat T7). The webhooks also
  get a hard request-body-size cap beyond the application-level payload cap
  (`StoreNotificationEnvelope.MaxRawPayloadLength`): a body over the cap is rejected `413`
  before it is buffered or handed to the parser.
- A **per-principal global** limiter on the authenticated surface
  (`RateLimiterOptions.GlobalLimiter`), partitioned on the authenticated principal's OIDC
  issuer+subject pair (subjects are unique only per issuer), so one caller's burst cannot
  exhaust another's allowance (threats T5/T1). Anonymous infrastructure traffic (the
  `/health/*` and `/metrics` endpoints) is intentionally **not** throttled by the global
  limiter, so orchestration probes and the Prometheus scrape stay reachable.

Rate limiting runs after authentication (so the per-principal partition sees the principal)
and before authorization and the endpoints (so an excess request is rejected before any
per-call database work or the webhook's external parser runs). Every limit is **runtime
configuration** (`RateLimiting:*`, fail-safe to the default on a non-positive value) with
safe, generous defaults so normal traffic is unaffected — 300 requests / 60s per principal,
60 / 60s per webhook IP — and the whole feature can be disabled
(`RateLimiting__Enabled=false`) for a deployment that throttles at its edge instead, in
which case both limiters become no-ops and the middleware is inert. An excess request gets
`429 Too Many Requests` as RFC 7807 Problem Details with a `Retry-After` header and no
tenant/principal/resource detail (threat T7). Rate limiting is a coarse abuse ceiling
layered **on top of** the OIDC/tenant authorization every endpoint already enforces
server-side; it never widens authorization (`docs/07_SECURITY_THREAT_MODEL.md`), exactly
like the CORS allow-list. See `docs/13_SELF_HOSTING_REQUIREMENTS.md` for the configuration
keys.

### Graceful shutdown and SignalR sticky sessions

Both hosts **drain in-flight work on shutdown within a tuned window** (CORE-DEP-002), so a
**rolling restart does not abruptly cut an in-flight request**. On a termination signal the
host stops accepting new connections and drains: the API lets in-flight HTTP requests and
open SignalR connections complete, and the worker lets each background job loop's current
tick observe cancellation and unwind (the loops already honor the stopping token).
`HostOptions.ShutdownTimeout` bounds that drain, and both hosts now set it explicitly from
configuration (`Hosting:ShutdownTimeout`, a `TimeSpan`) with a tuned default of **25 seconds**
instead of leaving it at the implicit framework default — one window applied identically to
the API and the worker (`apps/api/Hosting/GracefulShutdownConfiguration.cs`). The window is
read from configuration only (a present-but-malformed or non-positive value is rejected at
startup) and must be kept **at or below** the orchestration termination grace period
(Kubernetes `terminationGracePeriodSeconds`, default 30s; the Compose `stop_grace_period`) so
the process exits cleanly before SIGKILL — the 25s default sits a few seconds under the
conventional 30s for headroom.

A **multi-instance** SignalR deployment additionally requires **sticky sessions / ARR
affinity** at the reverse proxy for the `/hubs` endpoint, _on top of_ the Redis/Valkey
backplane (CORE-OPS-007): a SignalR connection starts with a **negotiate** request that issues
a `connectionId`, and the non-WebSocket fallbacks (Server-Sent Events, long polling) then make
further HTTP requests that **must reach the same instance** — without affinity the
negotiate/transport handshake breaks. Affinity (handshake pinning) and the backplane
(cross-instance event fan-out) solve different problems and are both required at scale.
Affinity is a proxy/edge concern, not a Core host setting; the proxy-specific configuration is
documented in `docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Graceful shutdown and SignalR
sticky-session affinity") and `docs/11_REALTIME_SYNC.md`.

## HTTP API and domain

The `/api/v1` surface and the domain it exposes: tenants, the current principal,
organizations, workspaces, sessions, participants, reveals, the audit log, the
participant feed, scene content and the resource-deletion commands.

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

The `Audience` is **mandatory in a production environment** (CORE-OPS-004). Token
validation only checks the audience when an `Audience` is configured, so a blank
`Audience` would silently disable audience scoping — any token the configured
issuer signs (including one minted for a different client/application of the same
issuer) would be accepted. To stop that foot-gun, when an `Authority` is configured and the
environment is `Production`, a blank `Audience` is treated as a misconfiguration
and **the host refuses to start** (it never serves a request with audience
validation off). Outside `Production` a blank `Audience` stays tolerated for local
development (the same latitude `Authentication__Oidc__RequireHttpsMetadata=false`
allows), and the unconfigured-`Authority` case keeps its fail-closed `401`
behavior.

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

| Method   | Route                                                         | Authorized callers                                                        |
| -------- | ------------------------------------------------------------- | ------------------------------------------------------------------------- |
| `GET`    | `/api/v1/workspaces`                                          | any workspace member (results filtered to the caller's memberships)       |
| `POST`   | `/api/v1/workspaces`                                          | organization `Owner` or `Admin`                                           |
| `GET`    | `/api/v1/workspaces/{workspaceId}`                            | members of that workspace                                                 |
| `PUT`    | `/api/v1/workspaces/{workspaceId}`                            | organization `Owner` or `Admin` (rename)                                  |
| `POST`   | `/api/v1/workspaces/{workspaceId}/archive`                    | organization `Owner` (archive — see below)                                |
| `POST`   | `/api/v1/workspaces/{workspaceId}/members`                    | organization `Owner` or `Admin` (create invite)                           |
| `GET`    | `/api/v1/workspaces/{workspaceId}/invitations`                | organization `Owner` or `Admin` (list pending invites — see below)        |
| `POST`   | `/api/v1/workspaces/{workspaceId}/invitations/accept`         | any authenticated org member who holds a valid token (redeem — see below) |
| `DELETE` | `/api/v1/workspaces/{workspaceId}/invitations/{invitationId}` | organization `Owner` or `Admin` (revoke invite — see below)               |
| `DELETE` | `/api/v1/workspaces/{workspaceId}/members/{memberId}`         | organization `Owner` or `Admin` (remove member — see below)               |

### Workspace member invites (scoped tokens)

`POST /api/v1/workspaces/{workspaceId}/members` creates a workspace invitation
with a single-use, scoped token. The token is generated with a cryptographically
secure RNG and is returned **once** in the creation response; only its SHA-256
hash is stored, and the token is never logged or returned again. Each token is
bound to one organization, one workspace, one role and an expiry, and is
single-use. It is a one-time join grant, not an authentication credential and
not a JWT (`docs/adr/0005-oidc-first-authentication.md`). Delivery and revocation
endpoints are follow-up stories.

### Workspace pending-invitations list (CORE-WS-008)

`GET /api/v1/workspaces/{workspaceId}/invitations` lists a workspace's **pending**
invitations so the manage-members surface can see its outstanding invites (the read
half of the invite flow; until now there was no way to see which invites were
outstanding). "Pending" is the lifecycle status only: an already-accepted or
already-revoked invitation is never listed. The route resolves its tenant from a
required `?organizationSlug=` query parameter (like the other workspace by-id routes).

The response is a **PII-safe projection** — id, invited email, role, status and expiry
(plus the tenant/workspace ids and the creation timestamp). The **invited email is the
only personal datum** and is included by design so an admin can see who was invited; it
remains data, never a credential. The **token hash is never returned** (there is no field
for it), and the one-time plaintext token does not exist on a stored invitation, so a read
can never expose it — the creation response is the only place a token is ever returned,
exactly once (threats T6/T7).

Authorization mirrors the member-invite/revoke routes on the same path: the **"Manage
members"** matrix row, **organization `Owner` or `Admin`** (`docs/06_AUTHORIZATION_MATRIX.md`),
matched exactly (`MembershipRole` is non-linear). Seeing the outstanding invites is itself
a manage-members capability, so a known tenant member who lacks `Owner`/`Admin` is `403`.
Every step is fail-closed and hidden as `404` for a caller who cannot see the tenant or a
workspace not in the resolved tenant, so a foreign or unknown workspace's invitations can
never be listed or probed for (threats T1/T5). The read is tenant- and workspace-scoped, so
invitations of another workspace or tenant are never returned.

### Workspace invitation acceptance (CORE-WS-006)

`POST /api/v1/workspaces/{workspaceId}/invitations/accept` redeems an invitation
into a workspace membership — the acceptance half of the invite flow, which makes
membership reachable over the public API for the first time. The scoped token is a
**bearer grant** (the decided model, threat T6): whoever presents a valid token
**becomes the member**, so the **authenticated caller's** OIDC subject — never the
invited email, which is data only — is granted the membership with the invitation's
role. The plaintext token is presented in the request **body**, never the URL path
or query string (a token in a URL leaks into access logs, proxies and history,
threat T7); the server hashes it and resolves the invitation by hash **within** the
route's workspace and the caller's resolved tenant, so a token minted for another
workspace or tenant resolves to nothing.

Redemption is **single-use, expiry- and revocation-aware and tenant/workspace-scoped**,
and it is **atomic** (`CORE-CONC-002`): the invitation is marked `Accepted`, the
`WorkspaceMember` is created and the join is audited (`MemberJoined`) in one
transaction, so a part-way failure rolls all three back. It is **fail-closed**: an
invalid, expired, revoked, already-redeemed or foreign token grants nothing and is
an **indistinguishable hidden `404`**. A caller who is already a member of the
workspace gets a `409` and does **not** consume the token. On PostgreSQL the
invitation carries the `xmin` concurrency token, so two concurrent redemptions of one
token cannot both grant a membership (the second conflicts with a `409`).

### Workspace invitation revoke (CORE-WS-007)

`DELETE /api/v1/workspaces/{workspaceId}/invitations/{invitationId}` revokes a
**pending** invitation so its scoped token **can never be redeemed** — the take-back
half of the invite flow and the threat T6 _revocation_ control made reachable for the
first time (until now an invite, once issued, could not be taken back). It is a soft
**`Pending -> Revoked` status transition**, not a delete: the invitation row survives so
its audit history is preserved. The route resolves its tenant from a required
`?organizationSlug=` query parameter (like the other workspace by-id routes).

Authorization mirrors the member-invite route on the same path: the **"Manage members"**
matrix row, **organization `Owner` or `Admin`** (`docs/06_AUTHORIZATION_MATRIX.md`),
matched exactly (`MembershipRole` is non-linear). Every step is fail-closed and hidden
as `404` for a caller who cannot see the tenant, a workspace not in the resolved tenant,
or an `invitationId` that belongs to another workspace/tenant — so an invitation outside
the caller's scope can never be revoked or probed for (threats T1/T5). A known tenant
member who lacks `Owner`/`Admin` is `403`.

Only a **pending** invitation may be revoked: an already-accepted invitation must not
silently undo a granted membership, and an already-revoked one is a no-op, so both are a
`409 Conflict` that changes nothing (placed **after** the role check, so a
non-`Owner`/`Admin` still gets a `403` and never learns the invitation state). The `409`
detail deliberately does not distinguish "accepted" from "revoked" (threat T7). A
successful revoke returns `204 No Content`; a subsequent redeem of the token is rejected
as the same **indistinguishable hidden `404`** as any other non-redeemable token.

Every successful revoke appends an append-only `MemberInvitationRevoked` audit record
(see "Audit log" below) capturing the tenant, the workspace, the authenticated **actor**
(the admin who revoked it), the revoked invitation and the `Pending -> Revoked` status
transition — never the invited email, the token or any content (threats T6/T7).

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
  a fail-closed denial); a denial emits nothing. CORE-MON-005 adds a final gate — the
  server-side **`session.participant.max`** participant cap (see "Server-side quota
  enforcement" below): once a session is at its plan participant limit a further join is a
  fail-closed `QuotaExceeded` denial that emits nothing, and the atomic check-and-consume
  (CORE-CONC-004) keeps concurrent joins from overrunning the cap.
- **Leave** — the symmetric `SessionParticipantLeaveService.LeaveAsync` removes a
  participant from a session's audience over the participant aggregate's soft-delete
  (`Participant.Remove`) and emits a `ParticipantLeft` event on (and only on) an **actual
  departure**. Removing an already-removed participant is an idempotent no-op that emits
  nothing, so each real departure appends **exactly one** event and a repeat appends none.
  On an actual departure it also **releases** the `session.participant.max` slot the join
  consumed (CORE-MON-005), so the cap reflects the session's current participants.

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
`event_type` string column.

CORE-PRS-001 wires the **real entry point** so presence works end-to-end: the two services
above were DI-registered but had **no production caller**, so the catalog's
`ParticipantJoined`/`ParticipantLeft` were dead code at go-live. The join/leave HTTP routes
now drive them (reusing the services — no parallel join/leave logic):

| Method | Route                                                             | Authorized callers                             |
| ------ | ----------------------------------------------------------------- | ---------------------------------------------- |
| `POST` | `/api/v1/sessions/{sessionId}/participants/{participantId}/join`  | workspace `Owner`, `Admin`, `Host` or `CoHost` |
| `POST` | `/api/v1/sessions/{sessionId}/participants/{participantId}/leave` | workspace `Owner`, `Admin`, `Host` or `CoHost` |

Like the lifecycle routes the target organization is a required `organizationSlug` **query**
parameter (the path carries no organization), turned into a trusted `TenantContext` by the
`TenantContextResolver` (token claim **and** persisted membership, threat T5); the session is
then loaded within that tenant, its workspace discovered from the loaded row, and the command
authorized by the caller's role in the **session's own workspace**. Managing presence is a
session-control action, so the authorized roles are exactly the `Owner`/`Admin`/`Host`/`CoHost`
set the start/end/cancel commands use — a non-member is hidden as `404` (never learns the
session exists), a known member without a control role is `403`, and a foreign-tenant session
is hidden as `404` (threats T1/T5). Object-level participant decisions stay inside the reused
services: a participant outside the session's tenant/workspace is a hidden `404`, a removed
participant or an ended session is `409`, and a join that would exceed the
`session.participant.max` cap is `409` (the limit, not the caller, is the reason) — so the
free-tier participant cap (CORE-MON-005) is now enforced on this **real** join path. A leave is
**idempotent** (an already-left participant is a `200` no-op that emits no second event). Every
denied or rejected command emits **no** event, so it can never leak a presence event to the
audience (fail-closed; threats T1/T3/T5), and the response is the identifier-only
`ParticipantPresenceResponse` (the session id, the participant id and a generic outcome name) —
never a participant display name or any PII (threat T7). No new DI registration, table or
migration is required; the persisted participant connection metadata remains later work.

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

There is at most **one active rule per `(session, resource, dimension)`**
(CORE-SVIS-002): the `visibility_rules` table carries two filtered unique indexes
(one for the audience-wide dimension where `target_participant_id IS NULL`, one per
selected participant where it `IS NOT NULL`), and the command inserts-on-conflict.
So two **concurrent first-reveals** of one resource — which carry **different**
idempotency keys and so do not short-circuit each other — cannot create two rules:
the loser of the create race is reported as a duplicate and converges onto the one
rule (it returns `Applied` but signals no change, so no duplicate event is emitted).
This closes the **ghost-reveal** hole where two rules were created and a later hide
flipped only one, leaving the other `Visible` as an un-hideable ghost; with one rule
per dimension a hide always **fully reverses** the reveal (no resource stays visible
after a successful hide; threats T5/T3). See `docs/10_DATABASE_SCHEMA.md`.

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

### Session-event catalog completeness

The session-event catalog is a **contract, not aspirational** (CORE-EVT-004, the session-event
analogue of CORE-SPEC-002): every event in `csv/event_catalog.csv` is now either **emitted** by a
Core command or explicitly **deferred** (with a named owner) or **removed**, and the
spec-consistency check (`scripts/spec-consistency.ps1`, **check 11**) validates the emitted set —
the `public const string` members of `apps/api/Realtime/SessionEventTypes.cs` — against the
**non-deferred** catalog, in both directions, so the catalog can no longer list a session event
that no command emits.

- **`SessionCreated`** — emitted **host-only** on session create (`POST /api/v1/workspaces/{workspaceId}/sessions`),
  appended to the new session's stream **atomically with the session row** (one unit of work,
  CORE-CONC-002) and delivered after the commit. The payload carries the session **id** and its
  `Prepared` status only.
- **`RecapGenerated`** — emitted **host-only** by the background recap worker when a recap is
  produced for an ended session, appended to the recap's session stream as a **system** event (no
  actor). The payload carries the recap and session **ids** only, never the recap body.

Both are **host-only** events (`SessionEventTypes.IsHostOnly`): the recipient resolver delivers
them to the session **hosts only** — never an observer or participant — both live and on reconnect
replay, so a created session or a generated recap never leaks to the audience (the catalog's "not
always participant-visible" / "participant recap requires separate reveal"; threats T2/T7). Unlike
`SceneActivated`, whose audience tracks the scene's current visibility, this is a subject-independent
host-facing routing class, so it **narrows** delivery and can never widen an audience.

`SceneCreated` and `ContentBlockCreated` stay in the catalog marked **deferred**: a scene/content
block is workspace-prepared and carries **no session**, so it cannot be a session-scoped event until
a session binds it (the Sessions active-scene pointer, the named owner). The three vertical/future
events `PrivateMessageSent`, `AssetRevealed` and `SessionNoteCreated` were **removed** (no Core
command). See `docs/24_SPEC_CONSISTENCY.md`.

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
`MemberRemoved`, `MemberInvitationRevoked`, `EntityDeleted`, `ContentBlockDeleted`, `SceneDeleted`, `AssetDeleted`, `WorkspaceArchived`,
`SessionCancelled`, plus the entitlement/store actions `EntitlementGranted`, `EntitlementRevoked`,
`QuotaExceeded`, `PurchaseVerificationSubmitted`, `PurchaseVerificationSucceeded`, `PurchaseVerificationFailed`,
`StoreNotificationReceived`, `StoreNotificationProcessed` added by CORE-SPEC-002, below) is
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

CORE-SPEC-002 (the `Specification Hardening` epic) backs the **entitlement/store** event catalog
(`csv/entitlement_event_catalog.csv`) with real audit actions, so that catalog is a contract, not aspirational.
It marked eight events `audit=true` but `AuditAction` carried none of them, so the claims were unbacked. The
story adds the eight actions and **emits** each on the action it names: `EntitlementGranted`/`EntitlementRevoked`
by `ProductEntitlementGrantService` (the CORE-MON-003/004 grant/revoke), `QuotaExceeded` at the quota-denial
sites (workspace create, session create/start, participant join, asset upload-intent), the three
`PurchaseVerification*` by the Apple/Google verification endpoints, and the two `StoreNotification*` by
`StoreNotificationService`; `scripts/spec-consistency.ps1` now binds the catalog to the enum (`audit=true` **iff**
a matching `AuditAction` member exists), so it can never drift back to aspirational without failing CI. Because a
purchase and the entitlement it grants are **deployment-spanning, not tenant-scoped** (a user's premium follows
the user's purchase, not an organization; `purchase_transactions` has no `organization_id` — `docs/21`), the
grant/revoke, purchase-verification and store-notification facts are recorded as **platform-level** audit facts:
`audit_logs.organization_id` is now **nullable** (the only schema change, `docs/adr/0014-platform-level-audit-facts.md`),
and such facts are append-only but stand **outside** the per-tenant tamper-evident hash chain (CORE-SEC-003) — the
same append-only posture the `purchase_events` trail has. `QuotaExceeded` stays a normal tenant-scoped, chained
fact (it is denied inside an already tenant-scoped command). The tenant-scoped reads filter by a concrete
organization, so a platform fact is never returned through any tenant's id (threat T5), and only identifiers,
enum names and generic descriptors are recorded — never a receipt, proof, token or payload (threat T7).

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
tenant's records are never returned through another tenant's id — threat T5), so the audit
query endpoint composes the trusted tenant resolution, this permission and the projection
exactly as the export/recap projectors are the reusable core their later endpoints sit on.

#### Reading the audit log over HTTP

CORE-SEC-002 (the `Security Hardening` epic) adds the missing **read** route, so the
append-only log is no longer write-only over HTTP and the dedicated `Auditor` role can
finally fulfil its sole purpose:

| Method | Route                | Authorized callers                       |
| ------ | -------------------- | ---------------------------------------- |
| `GET`  | `/api/v1/audit-logs` | organization `Owner`, `Admin`, `Auditor` |

The route is built **on top of** the existing `AuditQueryPolicy` and `AuditLogEntryView`
(no parallel audit engine). The tenant is the required `?organizationSlug=` query
parameter (the path carries no organization), resolved by the same
`TenantContextResolver` (token organization claim **and** persisted membership) the other
tenant-scoped routes use, so the read is **tenant-scoped** — entries are loaded only
through `IAuditLogRepository.ListPageByOrganizationAsync` for the resolved tenant and one
tenant's records are never returned through another's id (threat T5). It is **paged**: the
optional `?limit=` (default 50, server-clamped to a max of 200) and zero-based `?offset=`
bound each page, and a `hasMore` flag tells the client whether a further page exists, so
an unbounded log is never returned whole.

Authorization is server-side and fail-closed (`docs/06_AUTHORIZATION_MATRIX.md` "View
audit log"): a service-account principal, a foreign/unclaimed/unknown tenant and a
non-member are all hidden as `404` (indistinguishable from a missing resource), and a
**known tenant member who lacks the `Owner`/`Admin`/`Auditor` grant is `403`** (the exact,
non-linear set-membership check; `Host`'s matrix-`optional` grant fails closed and is
denied). The paging parameters are validated only **after** authorization, so an
unauthorized caller never receives request-shape feedback. The result is the
`AuditQueryPolicy.Project` projection into `AuditLogEntryView` — identifiers, enums and
generic state names only, never a display name, email, token, storage coordinate or
resolved content (threat T7) — with the deny-by-default empty-set backstop kept in one
place.

#### Tamper-evident audit log

CORE-SEC-003 (the `Security Hardening` epic) makes the append-only log **tamper-evident**, so a DB-level actor
or a future regression that alters or deletes a persisted row directly — not through the immutable append API —
is **detectable**. The application-level append-only guarantee was real but had no defence below it; the hash
chain closes that gap.

Every entry is sealed into a **per-tenant SHA-256 hash chain** at append time. Three columns on `audit_logs`
carry it: a `sequence` (a per-tenant, gap-free, strictly monotonic **append** number — the chain's spine,
distinct from the event-time-ordered surrogate id), a `previous_hash` (the link to the preceding entry's hash,
`null` for a tenant's genesis entry) and an `entry_hash` (a SHA-256 over the entry's recorded fields plus the
previous hash). Changing, deleting, inserting or reordering a row breaks the chain. The sequence numbers come
from an `audit_log_sequences` counter the append path increments with a single atomic
`INSERT ... ON CONFLICT DO UPDATE`, whose row lock **serializes** concurrent same-tenant appends so the chain
never forks — the audit analogue of the per-session event sequence (CORE-RTC-001), scoped to the tenant. The
increment runs inside the command's unit-of-work transaction (CORE-CONC-002), so a rollback reclaims the number
and the chain stays gap-free; the unique `audit_logs(organization_id, sequence)` index is the integrity
backstop.

The **verification routine** (`AuditLogChainVerifier`) reads a tenant's chain in append order and reports
whether it is intact, pinpointing the first altered/deleted/reordered entry. It is **tenant-scoped** (a break in
one tenant never implicates another) and content-free (identifiers and counts only, threat T7). The **read
contract is unchanged**: the existing append + tenant-scoped reads keep their shape; verification is a separate
routine.

The chain is an **unsigned** SHA-256 chain (no secret key): it detects accidental corruption, an isolated row
edit/deletion and a bypass of the append path. As **defence in depth**, a deployment also REVOKEs `UPDATE` and
`DELETE` on `audit_logs` from the runtime application role (the app only appends and reads) so history cannot be
rewritten at all — see `docs/13_SELF_HOSTING_REQUIREMENTS.md`. Cryptographic signing or external anchoring
against a fully privileged actor is a documented follow-up.

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

| Method | Route                                                       | Authorized callers                             |
| ------ | ----------------------------------------------------------- | ---------------------------------------------- |
| `GET`  | `/api/v1/workspaces/{workspaceId}/scenes`                   | any member of that workspace                   |
| `POST` | `/api/v1/workspaces/{workspaceId}/scenes`                   | workspace `Owner`, `Admin`, `Host` or `CoHost` |
| `POST` | `/api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder` | workspace `Owner`, `Admin`, `Host` or `CoHost` |
| `GET`  | `/api/v1/scenes/{sceneId}`                                  | any member of the scene's workspace            |
| `POST` | `/api/v1/scenes/{sceneId}/content-blocks`                   | workspace `Owner`, `Admin`, `Host` or `CoHost` |

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
current last scene in the workspace). Reordering a scene
(`POST .../scenes/{sceneId}/reorder`) moves it to a client-supplied **0-based list
position** (`targetIndex`), but the actual `scene_order` values are still assigned
server-side: the server re-packs the workspace's scenes to a contiguous, gap-free,
duplicate-free ordering and returns the new sequence (`200 OK`). A position at or
beyond the end moves the scene to the last slot (clamped); a negative position is
`400`. Reordering is host-only (`Owner`/`Admin`/`Host`/`CoHost`) and a benign
metadata move, so it is not audited — but it **is** a genuine read-modify-write on
the scene rows, so two interleaved reorders are guarded by the scene's optimistic
concurrency token: the loser gets a `409` rather than corrupting the ordering.
Clients therefore never supply or collide an absolute order. Creating a content
block stores it at its initial revision. Both creates return `201 Created`.

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

## Realtime

The SignalR realtime hub that streams role-filtered session events to connected
participants.

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
live, and a hidden event is never replayed. The optional `?afterSequence=` is the caller's
last acknowledged per-session **sequence** number; events with a greater sequence are replayed,
so a cursor of N returns N+1.. with no skips or duplicates (a cursor below the first replays the
whole stream, which the client deduplicates per `docs/11_REALTIME_SYNC.md`).
Like the participant-visible feed, the stream is private: every denial — a foreign tenant,
an unknown session, a caller with no legitimate relationship, or a `participantId` the
caller does not own — is hidden as `404` (never `403`).

**Per-session monotonic event sequence (CORE-RTC-001).** Every session event carries a
`sequence` column — a **per-session, gap-free, strictly monotonic** number — and both live
ordering and reconnect replay use **that sequence** (`session_events(session_id, sequence)`,
a unique index) rather than the UUIDv7 `eventId`. The id is only monotonic at **millisecond**
resolution, so events appended within one millisecond reorder under an id-ordered read — and a
single reveal publishes `ContentRevealed` + `VisibilityRuleChanged` + (for a scene) `SceneActivated`
at the **same** instant — whereas ordering by the sequence preserves their append order. The
numbers are handed out by a per-session `session_event_sequences` counter the append path
increments with a single atomic `INSERT … ON CONFLICT … DO UPDATE`, whose row lock **serializes**
concurrent appends to a session (the second blocks and increments from the committed value rather
than colliding); the increment runs **inside** the command's unit-of-work transaction
(CORE-CONC-002) with the event insert, so a rollback reclaims the number and the stream stays
gap-free. The sequence travels in every delivered envelope and every replay item, so a **client
detects a missed event as a gap** in the sequence (`docs/10_DATABASE_SCHEMA.md`,
`docs/11_REALTIME_SYNC.md`). This supersedes the earlier read that ordered the stream by
`event_id` — the previously documented critical index `session_events(session_id, created_at,
event_id)` is retained only for time-range queries.

**Scale-out abstraction (CORE-RT-006).** `docs/11_REALTIME_SYNC.md` ("Scale-out") calls for a
"Valkey/Redis-compatible backplane later when multiple API instances run". The Realtime module now defines
that seam: `IRealtimeBackplane` is the single transport boundary a server-computed event delivery crosses
on its way to the connected clients. The `InProcessRealtimeBackplane` fans each delivery out to its
server-computed SignalR group over the hub (`IHubContext<SessionHub>`, part of the shared framework — no new
dependency). On a single API instance that reaches only the connections held by **this** instance; the actual
cross-instance scale-out is wired by CORE-OPS-007 below, which makes the SignalR backplane Redis/Valkey-backed
so the **same** group send also reaches connections held by **other** instances.

The backplane receives an **already-authorized** delivery — one recipient-safe payload addressed to exactly
**one** server-managed group (`RealtimeGroups`), produced by the per-recipient recipient resolver
(CORE-RT-004) and only ever invoked by the publisher. It has no event, no visibility subject and no way to
enumerate recipients, so it **cannot** widen the audience: it only forwards what the resolver already
authorized. The per-recipient recipient computation therefore stays the **single send path**, and
"Realtime delivery never leaks hidden events" (threat T3 in `docs/07_SECURITY_THREAT_MODEL.md`) holds for
every backplane — in-process or scaled-out — by construction.

**Redis/Valkey scale-out backplane (CORE-OPS-007).** A SignalR hub tracks group membership **per process**,
so with more than one API instance an event computed on one instance reaches only the clients connected to
**that** instance and is silently dropped for clients on the others. Core now wires the official ASP.NET Core
SignalR backplane (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`) **conditionally** on the deployment's
`Realtime:Backplane:*` configuration (`AddLiveCoreRealtime`):

- **Configured** (a connection string is present at `Realtime:Backplane:ConnectionString`): `AddStackExchangeRedis`
  replaces the in-memory SignalR `HubLifetimeManager` with the Redis-backed one, so every group send through
  `IHubContext<SessionHub>` is published over Redis pub/sub and reaches the connections held by **every**
  instance — realtime events reach clients across multiple API instances.
- **Unconfigured:** SignalR keeps its in-memory `HubLifetimeManager` — correct for a **single** API instance
  only (the documented single-instance constraint; a multi-replica deployment must configure a backplane).

Enabling the backplane swaps only the **transport beneath** `IHubContext`: the `IRealtimeBackplane` stays the
same `InProcessRealtimeBackplane`, the publisher still hands it one already-authorized per-recipient delivery,
and the per-recipient recipient computation is **unchanged** — so the Redis backplane only transports an
already-authorized group send and **cannot widen the audience** (threat T3) whether or not it is configured.
The connection string is supplied at runtime via configuration only — no backplane connection string lives in
this repository (threat T7); the Redis/Valkey server and its connection string belong to the deployment
(`docs/13_SELF_HOSTING_REQUIREMENTS.md`).

The `SessionStarted`/`SessionEnded` lifecycle events are wired over this delivery path by CORE-EVT-001 (see
"Session lifecycle commands"): the start/end endpoints publish them through `ISessionEventPublisher` as
**subjectless** audience events, so the recipient resolver delivers each to the whole session audience and
reconnect replay re-delivers them. Wiring the remaining catalog events over this delivery path is a later
Realtime story (`docs/11_REALTIME_SYNC.md`).

**Connection re-authorization / eviction (CORE-RTC-002).** A connection's server-managed groups are resolved
**once**, at connect, so a caller whose standing changes **mid-session** would keep receiving events their old
standing allowed until they reconnected: a removed participant's still-open socket stays in its participant
group, and a demoted host keeps the host/observer group deliveries (the participant audience fan-out is
re-gated per event by the recipient resolver — it enumerates only **active** participants — but group
**membership** is not). The Realtime module now closes that gap with an **eviction** seam
(`IRealtimeConnectionEvictor`, backed by a singleton `RealtimeConnectionRegistry`): the hub records each
admitted connection's server-computed authorized facts + an abort handle on connect (and clears it on
disconnect), and a removal/role-change command raises the seam to **abort** exactly the affected connections —
the removed participant's (raised by the `SessionParticipantLeaveService` / `Participant.Remove` flow), or the
demoted member's host/observer connections (raised by a workspace role-change command). Aborting the socket
stops it receiving events **immediately**, not only on reconnect, and is matched by the full
tenant/workspace/session (and participant or subject) tuple, so a connection in another session, workspace or
tenant is never touched (threats T1/T5); a membership role change never aborts the subject's separate
participant connection, and vice-versa.

Eviction **only ever removes** a connection — it never adds one to a group and never sends an event — so it
can never widen an audience (threat T3). The authoritative re-admission stays the **same** connection resolver:
an evicted client that reconnects is authorized from scratch (a demoted host re-joins only its new role's
groups; a removed participant is denied), so this reuses the single authorization path rather than duplicating
it. The registry tracks the connections of the instance it runs on (the abort handle is an in-process
`HubCallerContext.Abort`), so it evicts on **that** instance immediately; the always-on cross-instance backstop
is the per-event recipient computation that already re-gates the participant audience fan-out, and propagating
the host/observer eviction signal across instances is a documented follow-up — the same single-instance posture
as the in-process backplane (`docs/11_REALTIME_SYNC.md`).

## Assets, storage and background jobs

Configuration secrets and the storage adapter, the asset lifecycle (metadata,
upload intent, signed download, linking and deletion), and the background worker
jobs (asset cleanup, recap generation, export processing and store-notification
reconciliation) with their exports, manifests, recaps and templates.

### Secret management and the configuration contract

Core holds **no secret in source**: every connection string, identity setting and credential is supplied at
runtime as configuration, and the repository ships only the **names** of those settings (CORE-OPS-008; threat
T7 in `docs/07_SECURITY_THREAT_MODEL.md`). A names-only [`.env.example`](.env.example) at the repository root is
the single, authoritative list of every setting the API and worker read, grouped by concern and annotated
`[secret]` / `[prod-required]`; copy it to a git-ignored `.env` and fill in real values. The env-var →
secret-store mapping (Kubernetes `Secret`/`ConfigMap`, Railway variables, Docker secrets) and the full
setting-by-setting contract table live in `docs/13_SELF_HOSTING_REQUIREMENTS.md`. .NET reads the hierarchical
key `A:B:C` from the environment variable `A__B__C` (double underscore).

The host **validates the contract at startup and fails loudly**, reusing the existing
fail-closed-when-unconfigured posture rather than adding a new one (`ProductionConfigurationValidator`). Outside
`Production` the contract is inert (a local run with no database or identity provider still starts and fails
closed). In `Production`, when a required value (`ConnectionStrings:Database`, `Authentication:Oidc:Authority`,
`Authentication:Oidc:Audience`) is missing, the host logs a loud, **named `Critical`** startup error listing
exactly which settings are unset — the key names only, never the configured values, so a secret is never
written to the log (threat T7) — and does not crash an otherwise-live process: it stays up, fails closed
(`401`/`503`) and reports **not-ready** (CORE-OPS-005), so orchestration never routes traffic at it. The one
hard fail-to-start case stays the OIDC audience foot-gun (CORE-OPS-004): a configured `Authority` with a blank
`Audience` refuses to start because it would silently disable audience scoping.

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

A **concrete S3-compatible adapter** now ships in Core (CORE-OPS-006, below); the
object-storage **endpoint and credentials** are still supplied by the deployment
through configuration only — Core holds no storage credentials in source
(`docs/13_SELF_HOSTING_REQUIREMENTS.md`; threat T7). The concrete adapter is
selected **conditionally** on that configuration; when storage is unconfigured the
default registration stays the **fail-closed** `UnconfiguredAssetStorage`: every
operation throws `AssetStorageNotConfiguredException` rather than serving bytes
some insecure way, so the private-by-default posture holds even when storage is
not configured (mirroring how the host runs without a database connection string
or OIDC authority and denies cleanly).

### Concrete S3-compatible storage adapter

CORE-OPS-006 implements the concrete `IAssetStorage` over the **AWS SDK for .NET**
S3 client (`AWSSDK.S3`), so a deployment that configures object storage gets
**real, SigV4 pre-signed** upload/download URLs against any S3-compatible backend —
RustFS self-hosted or any S3-compatible provider hosted
(`docs/02_ARCHITECTURE.md`; `docs/12_STORAGE_ASSETS.md`; ADR 0006). The pre-signed
URL is computed **locally** by the SDK (no network round-trip); `DeleteObjectAsync`
performs a real, server-side object delete with the deployment's own credentials
(no URL is handed to any client). The adapter signs **only the given asset's own
bucket + object key** and re-validates the result through `SignedAssetUrl`
(absolute, lifetime ≤ one hour), so it cannot mint a public, long-lived or
cross-object URL (threats T4/T5/T1).

`S3CompatibleAssetStorage` is registered **conditionally** by
`AddAssetStorage(configuration)` (used by **both** the API host and the worker's
cleanup job, so the two never diverge): with `Assets:Storage:Endpoint`,
`Assets:Storage:AccessKeyId` and `Assets:Storage:SecretAccessKey` all configured it
wires the concrete adapter; with nothing (or a **partial**) configuration it keeps
the fail-closed `UnconfiguredAssetStorage`, so unconfigured storage still denies
cleanly (the consuming endpoints return `503`). Optional settings are
`Assets:Storage:Region` (default `us-east-1`), `Assets:Storage:ForcePathStyle`
(default `true`, what self-hosted backends need) and `Assets:Storage:UrlLifetime`
(default 15 minutes, validated `> 0` and `≤ 1h`). `Assets:Storage:Bucket` /
`:Provider` remain the per-asset naming (`AssetStorageLocation`). **No storage
credential is read anywhere but configuration** (e.g. the environment variables
`Assets__Storage__AccessKeyId` / `Assets__Storage__SecretAccessKey`); none live in
the repository (threat T7).

Adding `AWSSDK.S3` is a **justified new dependency**: minting S3 SigV4 pre-signed
URLs is security-sensitive cryptography that should use the official, maintained
SDK rather than a hand-rolled signer.

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
**reuses** the central Visibility engine (visibility logic is not duplicated). A reveal is
**session-scoped** (ADR 0013), so **every** audience download is authorized against the
**session-scoped** visibility of the linked resource (CORE-SVIS-003 for the participant path,
CORE-SVIS-004 for the observer role-level path — the workspace-wide overload has been removed).
An **audience** role (`Participant`/`Observer`) supplies a `?sessionId=` and may download an
asset **only** when it is linked to a content block or entity **visible to them in that
session** — never one revealed only in a **sibling session** of the same workspace, nor (for a
participant) one revealed only to **another** participant; host-content roles
(`Owner`/`Admin`/`Host`/`CoHost`) may always download and need no session; the audit role and
any undefined role are **denied fail-closed**. The asset stays **private by default** and is
reached only through the single short-lived signed URL minted after the permission check (the
epic acceptance criterion; threat T4 "Asset leak"; threat T2 visibility leak).

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
`apps/worker/Dockerfile`); at the time it served no HTTP traffic and exposed no port (CORE-DR-003 later
added the worker's `/metrics` + `/health/live` surface on port 9464).

### Recap generation job

CORE-JOB-001 adds the Recaps module's background generation — the worker's second periodic job
(`apps/worker`; `docs/02_ARCHITECTURE.md`: the worker owns async jobs), behind no HTTP route. It
produces a recap for every session that **needs** one — a session that has **ended** but has no
recap yet — so the durable `Recap` record (CORE-AUD-004) is produced asynchronously rather than only
through a synchronous host path (`docs/00_START_HERE.md`: a Host can "produce Recaps"). Each recap is
**system-produced** (no user; `docs/09_EVENT_CATALOG.md` `RecapGenerated` source "System/Host") with a
generic, product-neutral body that **reflects what actually happened** in the session and is appended
through the existing `IRecapRepository`.

The recap body is **composed from the session's own append-only event stream** (CORE-RCP-002), not just
the start/end timestamps: the generation service reads the durable `session_events` through the existing
`ISessionEventRepository` (the same tenant- and session-scoped read reconnect replay uses) and
`RecapSummaryComposer` summarizes them into the UTC live timeline plus a fixed-order tally of the generic
event kinds the session recorded (scene activations, content reveals/hides, visibility changes,
participant joins/departures, …). The composition is **deterministic** (a pure function of the timeline
timestamps and the multiset of event-type names; non-UTC/edge timestamps are normalized to UTC) and **safe
on empty/partial streams** (a zero-event session yields a non-blank "no activity recorded" body). It is
**visibility-safe**: the recap body is host content that `RecapProjection` already keeps from the audience
until a separate reveal (it fails closed), and to keep even that host-only body free of resolved content
the composer reads **only each event's generic type name** — never the payload, the visibility subject, the
actor or the target — so the body carries aggregate counts and the timeline but never a session title, a
resource id or any free-form content (threats T2/T7). Core's automatic recap stays a generic continuation
record; a vertical that wants a richer narrative produces its own host recap through `IRecapRepository`.

It is **idempotent** and **tenant-scoped** (the acceptance criteria). Eligibility is read by
`IRecapEligibleSessionReader` as an anti-join — ended sessions with no recap — so a session that
already has a recap is never eligible and is recapped **at most once** across sequential sweeps;
the read spans all tenants (a system sweep) but each produced recap carries its own session's
organization, workspace and id, so a session in one tenant only ever receives a recap attributed to
that same tenant (threat T5). The eligibility read lives in the Recaps module because it already
depends on the Sessions module (a recap has a foreign key into `sessions`); it only **reads** session
coordinates, never writes the sessions table.

Idempotency holds **under concurrent workers** too (CORE-RCP-001). The eligibility read is a `NOT EXISTS`
read **decoupled** from the insert and the worker loop has no single-instance guard, so two overlapping
sweeps — in one process or across replicas — can both observe the same session as eligible and both try to
append. The authoritative guard is the partial unique index `recaps(session_id)` **where**
`generated_by IS NULL` (system recaps only; host recaps stay unconstrained): it permits exactly one system
recap per session, so the losing append is rejected and `RecapRepository.TryAppendSystemRecapAsync`
**converges onto the existing recap** (insert-on-conflict) — reported as a deduplicated no-op, never a
duplicate and never a failure. So **at most one system recap exists per session regardless of how many
replicas or overlapping sweeps run**, and the worker loop needs no single-instance guard. A genuine
persistence error (the session/workspace/tenant was deleted between the read and the append) still surfaces,
so the sweep leaves that session eligible and retries it.

The generation logic lives in the Recaps module (`RecapGenerationService`); the worker only schedules
it (`RecapGenerationBackgroundService`, every `Recaps:Generation:SweepInterval`, in bounded
`Recaps:Generation:BatchSize` batches), and like the cleanup job it is **gated on a configured database
connection string** (no database -> the worker starts but runs no generation loop). The sweep is
per-session **resilient**: a recap that fails to persist (for example because the session was deleted
between the eligibility read and the append) is logged and counted, and that session stays eligible for
the next sweep, without aborting the run. On a **freshly produced** recap (never a deduplicated race)
the job also appends the durable, **host-only** `RecapGenerated` session event to the recap's session
stream (CORE-EVT-004) — a system event with an identifier-only payload (the recap and session ids,
never the body; threat T7) — so a host learns on reconnect/replay that a recap exists while the
audience never does. The recap is the durable source of truth and is committed first; the event is
appended in its own transaction (the recap's not-exists-then-append dedup cannot share an enclosing
transaction without the partial-unique-index race aborting it), and a failed event append leaves the
recap intact. The `RecapGenerated` **audit fact** and any recap HTTP route remain follow-up stories.

### Export processing job

CORE-JOB-002 adds the Exports module's background processing — the worker's third periodic job
(`apps/worker`; `docs/02_ARCHITECTURE.md`: the worker owns "background jobs, exports, cleanup, async
processing"), behind no HTTP route. The export job and manifest aggregates already existed (CORE-AUD-002,
CORE-AUD-003) but nothing processed them; this job picks up **queued export jobs** and produces their
**workspace export manifests/entries**. For each queued job it inventories the workspace and records a
per-kind `ExportManifest` (one `ExportManifestEntry` per generic `ExportResourceKind` — sessions, scenes,
content blocks, entities, participants, assets), counting **rows only**, never any title, body, object key or
other content (threats T7/T8).

It drives each job through the `ExportJob` aggregate's own guarded **status transitions** (the acceptance
criterion): `Pending -> Running -> Completed`, committed **atomically** with the produced manifest as a single
unit of work (the repositories share one EF unit of work per sweep scope), so the job is never observed
completed without its manifest, nor a manifest without a completed job, and a crash before the commit simply
leaves the job queued for the next sweep to reprocess. The work reuses the existing Exports module
(`IExportJobRepository`, `ExportManifest.ForWorkspaceExport`, `IExportManifestRepository`); no parallel export
pipeline is built.

It is **idempotent** and **tenant-scoped** (the acceptance criteria). Eligibility is read by
`IQueuedExportJobReader` as the workspace-scoped jobs that are not yet terminal, so a completed export is never
re-processed and any job left non-terminal by an interrupted run is reprocessed by the next sweep; the
produce-exactly-one-manifest guarantee is the unique `export_manifests(export_job_id)` index, so a lost create
race (or a concurrent worker) surfaces as a benign duplicate whose losing attempt rolls back atomically. The read spans all
tenants (a system sweep), but each job is re-resolved, inventoried and manifested with its **own**
organization and workspace (re-scoped through `IExportJobRepository.FindByIdAsync`, the surrogate id never
trusted alone), so one workspace's export only ever counts its own resources and its manifest is attributed
only to its own tenant (threat T5). Only `Workspace`-scoped jobs are processed; a `UserData` export's narrower
manifest is a separate artifact that does not yet exist, so processing it is a follow-up and is never silently
widened into a workspace inventory (threat T8).

The processing logic lives in the Exports module (`ExportProcessingService`); the worker only schedules it
(`ExportProcessingBackgroundService`, every `Exports:Processing:SweepInterval`, in bounded
`Exports:Processing:BatchSize` batches), and like the other jobs it is **gated on a configured database
connection string** (no database -> the worker starts but runs no processing loop). The sweep is per-job
**resilient**: a job whose processing fails (for example because its workspace was deleted between the queued
read and processing) is logged and counted, and that job is left non-terminal for the next sweep to retry,
without aborting the run. An export-request HTTP route and a user-data export pipeline remain follow-up stories.

### Store notification reconciliation job

CORE-JOB-003 adds the Store module's background reconciliation — the worker's fourth periodic job
(`apps/worker`; `docs/02_ARCHITECTURE.md`: the worker owns "background jobs, ... async processing"), behind no
HTTP route. Store server notifications (renewals, cancellations, refunds, grace periods) are processed only on
the **synchronous inbound webhook** (CORE-STORE-005), in **delivery order** — but a store delivers at least
once and can **reorder or drop** deliveries, so a purchase's persisted status can drift from the status the
latest event implies: an older notification applied after a newer one (out-of-order), or a notification that
arrived before the purchase was ever recorded and so applied nothing (missed). This job re-derives each drifted
purchase's status from the recorded ledger and converges it, so **"missed or out-of-order store notifications
are reconciled so entitlement state converges; idempotent"** (the acceptance criterion).

It **re-derives entitlement state from `store_notification_events`** (extended this story with the store's
reported `occurred_at` event time) **and `purchase_events`** (the current purchase status is the head of that
append-only trail): the converged status is the **monotonic fold** of all the purchase's recorded notifications
in `occurred_at` order (CORE-MON-004 — a revoked state is absorbing, so a refund stays revoked even when a later
renewal was recorded after it), regardless of the order notifications were delivered or applied. It **reuses
`StoreNotificationService`** — the new `ReconcileTransactionAsync` converges a purchase by reusing the same
audited, idempotent `PurchaseTransactionService.ChangeStatusAsync` the webhook uses (stamped with the
authoritative notification's event time), so no parallel pipeline is built and every convergence is audited on
the `purchase_events` trail exactly as a webhook-driven change is. Drifted purchases come from
`IReconcilablePurchaseReader`; a reconciled purchase matches its converged status and drops out (and a purchase
already in a revoked/terminal state is never a candidate), so a bounded sweep makes progress.

It is **idempotent** and **fail-closed**. Reconciliation re-derives from immutable ledger facts and converges
to the same state every time (a consistent purchase is a no-op, no status change and no audit event), so a
re-run — or a sweep retried after a crash — changes nothing. A notification recorded for a purchase Core never
persisted converges nothing (`TransactionNotFound`): nothing is fabricated, so no entitlement is granted without
a real verified purchase behind it. Purchases are **global** (keyed only by provider + provider transaction id,
no tenant or buyer column, CORE-STORE-002), so there is no tenant boundary to scope on this system job.

Crucially, unlike the other worker jobs it is **gated on billing**. Store receipts/billing are **out of scope
for Core v1** (`docs/01_PRODUCT_VISION_AND_SCOPE.md`), so the job runs **only when a deployment explicitly
enables it** — both a configured database connection string **and** `Store:Reconciliation:Enabled=true`. With
the flag unset (the default) the worker registers no reconciliation loop — **"only runs when billing is
configured"**, the same fail-closed posture as the store verification/notification parser resolvers that
register no adapter until a deployment supplies one. The reconciliation logic lives in the Store module
(`StoreNotificationReconciliationService`, reusing `StoreNotificationService`); the worker only schedules it
(`StoreNotificationReconciliationBackgroundService`, every `Store:Reconciliation:SweepInterval`, in bounded
`Store:Reconciliation:BatchSize` batches). The sweep is per-purchase **resilient**: a purchase whose
convergence fails is logged and counted and left for the next sweep, without aborting the run. When the converged
status is a revoked state the sweep **revokes** the buyer-linked `SubjectEntitlement` too (CORE-MON-004, the
inverse of the grant chain — the missed-refund revoke path only reconciliation can apply); a SQL window-function
candidate query for high-volume deployments remains a follow-up.

### Worker liveness heartbeat

CORE-OPS-005 adds the worker's liveness signal so orchestration can detect a **wedged** job loop. A
loop is resilient to a sweep that _throws_ (it logs and retries on the next tick), but a sweep that
**hangs** — a stuck database or storage call that never returns — would leave the worker process alive
yet doing no work, which a process-liveness check cannot see. Because the worker serves no HTTP traffic
and exposes no port, the heartbeat is a **file**, not a health port: every job loop (asset cleanup,
recap generation, export processing and the billing-gated store-notification reconciliation) writes the
current UTC timestamp to `Worker:Heartbeat:FilePath` (default
`<temp>/livecore-worker.heartbeat`) on startup and after **every** completed sweep tick. The file is
the worker process's liveness signal, refreshed whenever a loop makes progress. The
heartbeat is wired **alongside** the jobs, so with no database there is no loop and no heartbeat
(nothing to stall). A heartbeat write never crashes the worker: a transient filesystem error is logged
and swallowed, and a persistent failure just makes the file go stale (fail-safe). It carries only a
timestamp — no identifiers, no secrets (threat T7).

CORE-DR-003 makes this **per-loop** and adds an HTTP surface. A single shared file let one healthy loop
keep the file fresh and **mask** three hung ones; now each loop beats its **own** file
(`<base>.<job>`), the worker serves a small HTTP surface (`Worker:Metrics:Url`, default port 9464), and a
new **`GET /health/live`** endpoint reports healthy only when **every** active loop's file is fresh
(within `Worker:Heartbeat:StaleAfter`, default 2 hours) — so a single hung loop is detectable over one
httpGet probe. The same surface serves the worker's Prometheus **`GET /metrics`** (see "Worker metrics and
per-loop liveness" above).

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
not access; CORE-AUD-002 is the export job model, its persistence and its EF migration only. The
export **read/download** route that retrieves a completed export's artifact is now mounted
(CORE-EXP-001, [Reading an export over HTTP](#reading-an-export-over-http)); an export-request HTTP
route and a user-data export pipeline remain later Exports stories.

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
not access; CORE-AUD-003 is the manifest model, its persistence, its EF migration and its role-based
projection only. The worker that actually **drives** the export and produces the manifest is
implemented — the export processing background job (CORE-JOB-002; see "Export processing job" above) —
and the export **read/download** route that retrieves the produced manifest as a completed export's
artifact is now mounted (CORE-EXP-001; see [Reading an export over HTTP](#reading-an-export-over-http)).

#### Reading an export over HTTP

CORE-EXP-001 (the `Vertical Authoring and Read API Completeness` epic) adds the export **read/download**
route on top of the existing repositories and projection — an export job and its manifest were produced
but had no HTTP surface, so an authorized host could not retrieve a completed export:

```text
GET /api/v1/exports/{exportId}?organizationSlug={slug}
```

In the Core model a completed workspace export's produced **artifact** is its `ExportManifest` — the
per-kind **table of contents** of what the export covered (counts only, never any exported scene/content
body; threats T7/T8). The Core stores no separate export blob in object storage, so the artifact is
delivered as an **authorized stream** — the role-projected manifest in this authenticated, authorized
response body — and **never** through a public or static URL: the manifest lives only in the
tenant-scoped database and is reachable only through this server-side permission check, exactly as the
asset flow never hands out a public bucket URL (threat T4). The route path carries only the `{exportId}`
(the export **job** id), so the target tenant is the required `?organizationSlug=` query parameter
(exactly like the asset signed-download and recap read routes).

Authorization is **object-level**, server-side and **fail-closed**, and — like the asset signed-download
flow — runs **before any artifact is produced** (`docs/06_AUTHORIZATION_MATRIX.md` "Export workspace";
threats T1/T5/T8). The trusted tenant is resolved from the token claim **and** persisted membership, the
export job is loaded within that tenant (so a foreign tenant's export is never reached even when the id
matches), and the caller must be a member of the export's **own** workspace. A service account, a
foreign/unknown tenant, an unknown export and a non-member are all hidden as **404** (never
distinguishable, never echoing why). A known member who is not an authorized downloader is **403**: the
authorized set is the "Export workspace" roles {Owner, Admin, Host} (`ExportAccessPolicy` — an exact,
non-linear set membership; CoHost, the audience roles Participant/Observer and the deployment-optional
Auditor all fail closed), so a **non-authoring** caller is denied outright and a participant never
receives any host-only export content. Only **after** authorization is the export's availability checked
(so an unauthorized caller never learns its state): an **incomplete or failed** export — anything not
`Completed`, or (defensively) a completed job with no manifest — has no retrievable artifact and is
**409** (mirroring the asset signed-download 409 for a still-pending asset). A downloadable, completed
export is returned **200** as its artifact role-projected through the same `ExportManifestProjection`
the worker journey (CORE-E2E-003) exercises (defence in depth: the export shape stays role-scoped even
though only full-view roles reach this point). A retention-based export **expiry** (a true `ExpiresAt`
with an object-storage purge) and a user-data export pipeline remain later stories
(`docs/24_SPEC_CONSISTENCY.md`).

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
the view **shape**, not access. CORE-AUD-004 was the recap model, its persistence, its EF migration
and its role-based projection only; the worker then produces a recap for every ended session
(CORE-RCP-001/002, [Recap generation job](#recap-generation-job)), and CORE-RCP-003 finally gives a
recap a read route ([Reading a recap over HTTP](#reading-a-recap-over-http)). The separate participant
**reveal** of a recap body remains a later story (`docs/24_SPEC_CONSISTENCY.md`).

#### Reading a recap over HTTP

CORE-RCP-003 (the `Vertical Authoring and Read API Completeness` epic) adds the recap **read** route on
top of the existing repository and projection — a recap was generated and persisted but had no HTTP
surface, so an end user could not retrieve it:

```text
GET /api/v1/sessions/{sessionId}/recap?organizationSlug={slug}
```

The route returns the session's **most-recently-generated** recap, **role-projected**. A session may
accumulate more than one recap (the worker produces at most one system recap per session; a host may
later add host recaps), so the singular route returns the last in produced order — today, where only
the system recap exists, that is the generated recap. The route path carries only `{sessionId}`, so
the target tenant is the required `?organizationSlug=` query parameter (exactly like the session
start/end commands).

Authorization is the **session read surface** (any workspace member may read; `csv/api_routes.csv`
roles "workspace members"), object-level and fail-closed (`docs/06_AUTHORIZATION_MATRIX.md`; threats
T1/T5/T8). The trusted tenant is resolved from the token claim **and** persisted membership, the
session is loaded within that tenant (so a foreign tenant's session is never reached even when the id
matches), and the caller must be a member of the session's **own** workspace — a service account, a
foreign/unknown tenant, an unknown session, a non-member, and a session with **no recap yet** are all
hidden as **404** (never distinguishable, never echoing why). There is no 403 path: the read is
allowed to every workspace member, and the only role effect is the response **shape**. A known member
receives the recap through the same `RecapProjection` the worker journey (CORE-E2E-003) exercises —
Owner/Admin/Host/CoHost/Auditor get the full `RecapView` **with** the body; Participant/Observer get
the host-only-field-stripped `RecapSummaryView` **without** the body, because a generated recap is host
content until a separate reveal (`docs/09_EVENT_CATALOG.md` `RecapGenerated`; threats T2/T8). The
typed SDK method for this route lands with the SDK-completion story (CORE-SDK-006).

## Entitlements, quotas and monetization

The product-neutral monetization surface: entitlement and plan definitions,
subject entitlements, quota definitions and enforcement, ad eligibility, the
mobile `/v1` path shape, purchase verification and persistence, buyer linkage and
store notifications.

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
(the "current user" of `GET /v1/me/entitlements`), an `EntitlementSubjectType.Workspace` (the subject of a
workspace-scoped entitlement) or an `EntitlementSubjectType.Session` (the subject of the per-session
`session.participant.max` participant cap, CORE-MON-005) — holds a granted `EntitlementDefinition` at a concrete
value. Its value shape is
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

| Command                              | Quota key (generic)       | Quota subject           | Behavior                                            |
| ------------------------------------ | ------------------------- | ----------------------- | --------------------------------------------------- |
| `POST /api/v1/workspaces`            | `workspace.active.max`    | the creating user       | increments on create                                |
| `POST /api/v1/sessions/{id}/start`   | `session.active.max`      | the session's workspace | increments on start                                 |
| `POST /api/v1/sessions/{id}/end`     | `session.active.max`      | the session's workspace | releases (decrements, clamped at zero) on end       |
| participant **join** (CORE-MON-005)  | `session.participant.max` | the session             | atomically check-and-consumes on admission          |
| participant **leave** (CORE-MON-005) | `session.participant.max` | the session             | releases (decrements, clamped at zero) on departure |

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

### Mobile API path shape (the `/v1` gateway)

The store and entitlement routes are documented in their **mobile-facing path shape** under a bare `/v1`
prefix (`csv/mobile_store_api_routes.csv`, e.g. `GET /v1/me/entitlements`,
`POST /v1/purchases/apple/transactions`), but every Core endpoint is mounted under the `/api/v1` prefix
`docs/08_API_CONTRACTS.md` mandates. CORE-MON-009 (the `Monetization v1` epic) makes a mobile client following
the documented `/v1/...` path reach the implemented endpoint **in-process**, so it no longer `404`s and no
external reverse-proxy rewrite is required.

`MobileApiGateway` (`apps/api/Hosting/MobileApiGateway.cs`, registered with
`builder.Services.AddLiveCoreMobileApiGateway()`) rewrites a request whose path matches one of the documented
mobile routes from its `/v1` path to the corresponding `/api/v1` path **before routing**, so it dispatches to
the **same** already-implemented endpoint. It is registered as an `IStartupFilter` precisely because the
original `/v1` path is unmounted: a startup filter's middleware runs ahead of the WebApplication's
automatically-added routing middleware, so the path is rewritten before the endpoint is selected without
re-ordering the curated request pipeline.

It is a pure, **scoped** addressing alias that adds **no endpoint, service, table or migration**:

- only the **exact** documented mobile routes (the in-code mirror of `csv/mobile_store_api_routes.csv`) are
  rewritten; any other `/v1/...` path is left untouched and still `404`s, so the rest of the Core API is never
  exposed under a second prefix (threats T1/T5);
- the target endpoint's authentication and server-side, tenant/subject authorization run **unchanged** — the
  rewrite touches only the request path and never the principal, the tenant boundary or the response, so it
  cannot widen authorization (an anonymous caller is still `401`, a service account still `403`, a foreign
  tenant still hidden), and it never reads or logs the token, the body or any tenant identifier (threat T7);
- the `{workspaceId}` segment of `/v1/workspaces/{workspaceId}/quota-status` matches any single path segment;
  the target endpoint validates the surrogate id as before.

Because it adds no `/api/v1` route, `csv/api_routes.csv`, the `docs/08` representative block and the
spec-consistency check are unchanged; `csv/mobile_store_api_routes.csv` now describes a resolvable surface.
See `docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md` "API surface" and `docs/24_SPEC_CONSISTENCY.md`.

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
request-shape feedback. CORE-MON-002 now **links the verified purchase to the authenticated buyer's subject** in
the same transaction (see "Buyer linkage for verified purchases" below), so a different subject submitting the same
external receipt is `409` and granted nothing; granting the resulting `SubjectEntitlement` from the linked buyer
(the product → plan → entitlement mapping) is the next story (CORE-MON-003). The Google purchase-token endpoint is
CORE-STORE-004 and idempotent store notifications are CORE-STORE-005.

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
unauthorized caller never receives request-shape feedback. CORE-MON-002 now **links the verified purchase to the
authenticated buyer's subject** in the same transaction (see "Buyer linkage for verified purchases" below), so a
different subject submitting the same external receipt is `409` and granted nothing; granting the resulting
`SubjectEntitlement` from the linked buyer is the next story (CORE-MON-003), and idempotent store notifications are
CORE-STORE-005.

### Buyer linkage for verified purchases

CORE-MON-002 (the buyer-linkage story of the **Monetization v1** epic) adds the missing link between a verified
purchase and **who bought it**, so a verified purchase can later grant **that subject** the mapped entitlement
(CORE-MON-003). `purchase_transactions` deliberately has **no buyer column** (CORE-STORE-002): a purchase is named
**globally** by its `(provider, provider_transaction_id)` pair, so two users submitting the same external receipt
collapse to one row and the authenticated buyer was verified then discarded. The new Store-owned
`billing_account_links` table records **which subject** a verified purchase belongs to.

Both verification endpoints (Apple CORE-STORE-003, Google CORE-STORE-004) now **record-then-link in one
transaction** (reusing the CORE-CONC-002 `TransactionalUnitOfWork`), so the buyer linkage is durably **atomic**
with the recording. The buyer is the **authenticated caller**, resolved server-side to their `users(id)` profile
(provisioned on first sight, exactly as `/me/entitlements` resolves the current user) and recorded as a generic
`User` subject — the **same subject shape** `subject_entitlements` uses, so the buyer-to-entitlement grant chain
reads it directly. The body carries no subject identity and no premium claim: **who** is buying is the token, not
anything the client asserts.

The acceptance criterion — "the same external receipt cannot be claimed by two different subjects" — is the
**unique `billing_account_links(purchase_transaction_id)` index**: a verified purchase is linkable to **only one**
subject. The same buyer re-submitting their own receipt is **idempotent** (no second row); a **different** subject
submitting the same receipt is denied **`409 Conflict`** and granted nothing (fail-closed — **user B can never
bind user A's receipt**), with a generic detail that reveals nothing about the owning subject (threats T5/T7).
There is **no tenant** on this route (a purchase is global), so the isolation that matters is **per-subject**: one
buyer's purchase is never claimable through another subject's identity, and a near-identity (the same subject id
under a different OIDC **issuer**) is a different user and is denied just the same. The link is **immutable** (which
subject a purchase belongs to never changes), is removed only when its purchase row is (the
`purchase_transaction_id` foreign key **cascades**), and stores only identifiers — never the verification proof or
any receipt content. This story adds the `billing_account_links` table and one EF migration.

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

Run the worker container (exposes its metrics/health port 9464; runs the asset cleanup, recap generation
and export processing jobs when a database is configured — plus the store-notification reconciliation job
when billing enables it — otherwise idles):

```bash
docker run --rm -p 9464:9464 livecore-worker
curl http://localhost:9464/health/live
curl http://localhost:9464/metrics
```

Image baseline:

- Both runtime images run as the non-root user built into the official .NET
  images (`USER $APP_UID`, a numeric UID so policies like `runAsNonRoot` can
  verify it).
- The runtime images contain only the published output: no SDK, no package
  caches, no build tooling.
- Both images expose an unprivileged port: the API on 8080, and the worker on
  9464 for its `/metrics` + per-loop `/health/live` surface (CORE-DR-003).
- The images define no `HEALTHCHECK` instruction on purpose: the .NET runtime
  images ship no HTTP client tooling, and none is installed just for probing.
  Orchestration platforms (Compose, Kubernetes, load balancers) probe
  `GET /health/live` (liveness) and `GET /health/ready` (readiness) over HTTP
  instead; the worker's liveness is its per-loop `GET /health/live`.
- Configuration is supplied at runtime through environment variables
  (for example `ASPNETCORE_ENVIRONMENT` and logging levels); no secrets are
  baked into the images.
- The base images are pinned by **immutable digest** (`...:10.0@sha256:...`), not
  the floating `10.0` tag, and the NuGet restore runs in **locked mode** against a
  committed `packages.lock.json`, so a rebuild always resolves the same base layers
  and the same dependency graph (CORE-DEP-003, see "Supply chain" below).

Local development orchestration (Compose with database, auth and storage
services) lives in `livecore-deploy`, not in this repository (see
`docs/13_SELF_HOSTING_REQUIREMENTS.md`).

### Publishing release images (CORE-OPS-009)

CI publishes the API and worker images to the GitHub Container Registry
(`ghcr.io`) **only on a release tag push** — never on a pull request or a branch
push, so an unreviewed or in-progress build is never published. Cut a release by
pushing an annotated SemVer tag (the same version the packages use, see
`docs/23_PACKAGE_VERSIONING.md`):

```bash
git tag -a v1.2.3 -m "Core v1.2.3"
git push origin v1.2.3
```

The tag push runs **every** quality gate (build, tests, format, boundary scan,
the container builds/smoke tests, migrations and the integration suite), and the
`publish` job runs only after they all pass. The published image references are
**immutable and versioned** — the image tag is the exact release version, never a
moving tag such as `latest`:

```text
ghcr.io/<owner>/livecore-api:1.2.3
ghcr.io/<owner>/livecore-worker:1.2.3
```

The tag derivation (`scripts/LiveCoreImageTags.psm1`, driven by
`scripts/derive-image-tags.ps1`) is fail-closed: only a `v<MAJOR>.<MINOR>.<PATCH>`
SemVer tag (optionally a prerelease) yields a reference; a branch, a pull
request, a moving tag (`latest`) or a malformed/build-metadata tag is rejected,
so the publish path can never produce a mutable or unversioned image tag. Before
pushing, the job refuses to overwrite a tag that already exists in the registry,
so a shipped version is never mutated. No registry credential is stored: the job
authenticates with the workflow's `GITHUB_TOKEN` (granted `packages: write` only
for that job). `scripts/test-image-tags.ps1` tests these properties and the
`publish-dry-run` job exercises the same derivation and build on every push and
pull request without pushing.

### Supply chain: pinned base images, SBOM and CVE scan (CORE-DEP-003)

The immutable release **tag** fixes what a deployment pulls; three further controls
fix what the image is built from and prove it carries no known-critical
vulnerability, so the layers underneath a version cannot silently drift and a
known-CVE base image cannot ship:

- **Base images pinned by digest.** `apps/api/Dockerfile`, `apps/worker/Dockerfile`
  and `apps/api/Migrations.Dockerfile` pin the .NET SDK and ASP.NET runtime base
  images by `sha256` digest (the readable `:10.0` tag is kept only for humans). Bump
  a digest deliberately with
  `docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0` and commit it.
- **Reproducible restore (locked mode).** `RestorePackagesWithLockFile` in
  `Directory.Build.props` makes every project commit a `packages.lock.json`, and the
  image builds restore in locked mode, so the build fails if the resolved package
  graph ever drifts from the committed lock file.
- **SBOM + CVE scan gate on publish.** The `publish` job builds each image, then —
  before any push — produces a CycloneDX **SBOM** and a vulnerability **scan report**
  (with Trivy) and runs the fail-closed gate (`scripts/assert-image-scan.ps1`): a
  **critical** vulnerability, a missing/empty SBOM, or an unreadable report fails the
  publish before the image is pushed. The SBOMs and reports are uploaded as the
  `supply-chain-attestations` build artifact, and the existing immutable-tag guard
  still runs before push.

The gate decision and the SBOM check are pure logic
(`scripts/LiveCoreImageScan.psm1`) tested from seeded fixtures by
`scripts/test-image-scan.ps1`, so "a seeded critical CVE fails the gate" is proven
deterministically on every push/pull request; the `publish-dry-run` job additionally
produces a real SBOM and scan report (running the gate in report-only mode so a
transient base-image CVE never blocks ordinary development). Cryptographic build
provenance/attestation (e.g. cosign) is a documented follow-up. See
`docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Pinned base images, SBOM and vulnerability
scan").

### Backup and restore (CORE-OPS-010)

The Core holds systems of record whose loss is unrecoverable: the tenant-isolated,
append-only audit trail (`audit_logs`), the session-event stream (`session_events`)
and the store purchase ledger (`purchase_transactions`, `purchase_events`,
`store_notification_events`), plus the private object-storage bucket of asset
binaries. A documented, **tested** backup/restore procedure covers all of them.

- `scripts/backup-livecore.ps1` runs a `pg_dump` (custom format) of the Core
  database and mirrors the private asset bucket, **encrypts the dump and the local
  asset mirror at rest** (CORE-DR-001), then writes a `livecore-backup-manifest.json`
  recording a row count and an order-independent content checksum for every system
  of record. It is fail-closed twice over: it refuses to run without an encryption
  passphrase (the audit, purchase-ledger and tenant data never land as plaintext),
  and it refuses to write a manifest that does not cover every system of record.
- `scripts/restore-livecore.ps1` decrypts and integrity-verifies the dump (failing
  closed on a wrong passphrase or a tampered artifact), restores it with
  `pg_restore` and the bucket from its decrypted mirror, then re-measures and
  verifies every system of record against the manifest, failing with a non-zero
  exit code if a record was dropped, altered or lost.
- `scripts/test-backup-restore-drill.ps1` is the runnable restore drill: a
  self-contained backup → restore → verify round-trip over a fixture modeling the
  systems of record, exercising the same coverage/integrity logic and the same
  encryption sink (`scripts/LiveCoreBackup.psm1`) and proving a faithful restore is
  accepted while a lossy, tampered or wrong-key one is rejected. It needs no
  database or object store and runs as the `backup-restore-drill` CI gate.
- `scripts/test-backup-restore-postgres.ps1` runs the **real** scripts against a
  live PostgreSQL (CORE-DR-002): the drill above proves the _logic_ with a fixture,
  but `pg_dump`/`pg_restore`/`psql` and the `to_jsonb` checksum were never run in CI,
  so a broken tool argument would ship green. It seeds every system-of-record table,
  runs the real `backup-livecore.ps1`, restores into a **fresh** database with the
  real `restore-livecore.ps1`, and asserts the full backup → restore → integrity
  round-trip (real `pg_dump`/`pg_restore` + `to_jsonb` row-count/checksum) passes —
  and that a restore which lost an append-only audit row is rejected fail-closed. It
  runs as the `backup-restore-postgres` CI gate against the same Postgres service the
  migrations/integration jobs use.

No credential is committed: the database password is read from the same
`ConnectionStrings:Database` value the API uses and passed via `PGPASSWORD`, and
the backup encryption passphrase is read from configuration
(`Backup__Encryption__Passphrase` or a file), never from source. The at-rest
encryption is a self-contained AES-256-CBC + HMAC-SHA256 sink (PBKDF2-HMAC-SHA256
key derivation) that uses only the .NET base class library, so it adds no
dependency and runs on both Windows PowerShell 5.1 and PowerShell 7+. The full
runbook (PostgreSQL `pg_dump` cadence and PITR, object-storage mirroring, backup
encryption and key management, the step-by-step restore drill, cadence/RPO/RTO and
security) lives in `docs/13_SELF_HOSTING_REQUIREMENTS.md`.

## Continuous integration

GitHub Actions runs `.github/workflows/ci.yml` on every push to `main`, on every
pull request, and on every release tag push (`v<MAJOR>.<MINOR>.<PATCH>`). All jobs
run on `ubuntu-latest` and execute the commands documented above verbatim:

| Job                       | What it runs                                                                                                                   |
| ------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| `dotnet`                  | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` on `LiveCore.slnx`                                          |
| `typescript`              | `pnpm install --frozen-lockfile`, `lint`, `format:check`, recursive `build` and `test`                                         |
| `boundary-scan`           | `pwsh -NoProfile -File scripts/boundary-scan.ps1` (forbidden vertical terms fail the build)                                    |
| `backup-restore-drill`    | `pwsh -NoProfile -File scripts/test-backup-restore-drill.ps1` (restore drill, CORE-OPS-010)                                    |
| `backup-restore-postgres` | seeds Postgres, runs the real `backup`/`restore` scripts and asserts the backup → restore → integrity round-trip (CORE-DR-002) |
| `powershell-lint`         | PSScriptAnalyzer (Error/Warning severity) over `scripts/*.ps1`                                                                 |
| `docker`                  | `docker build` for both Dockerfiles, then container smoke tests (`/health/live`, worker startup)                               |
| `publish-dry-run`         | `scripts/test-image-tags.ps1`, then a no-push dry-run build of the publish path (off a non-tag)                                |
| `migrations`              | builds the migrations runner image and applies all migrations to an empty Postgres                                             |
| `integration-postgres`    | model-vs-migration drift gate, then the integration suite against a real Postgres                                              |
| `publish`                 | **release tag only**: pushes immutable, versioned API and worker images to `ghcr.io` once the gates pass                       |

The `publish` job runs **only on a release tag** and **only after every other job
passes**; pull requests and branch pushes never reach it, so a registry push never
happens off a release (CORE-OPS-009). Line endings are normalized to LF in the repository via `.gitattributes`, so
the boundary scan and `dotnet format` behave identically on Linux CI and on
Windows working copies.

## License

This project is licensed under the GNU Affero General Public License v3.0 or later.

Commercial dual licensing may be offered in the future for organizations that require proprietary use, embedding, hosting, or distribution without AGPL obligations.

For commercial licensing inquiries, contact: singh.harwinder@outlook.com
