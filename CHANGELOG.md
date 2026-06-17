# Changelog

This file records notable changes to the published TypeScript packages of the
LiveCore Core Platform: `@livecore/contracts`, `@livecore/sdk-ts`,
`@livecore/design-tokens` and `@livecore/ui-core`.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the packages adhere to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The four packages are released together (lockstep), so they always share one
version. Each package also keeps its own `CHANGELOG.md` with the package-level
detail; this root file is the workspace-level summary. The .NET API and worker
hosts are not published packages and are not versioned here. See
`docs/23_PACKAGE_VERSIONING.md` for the full versioning and changelog process.

## [Unreleased]

### Added

- Third-party attribution and a CI license-compliance gate (CORE-LIC-003). A
  generated `THIRD-PARTY-NOTICES.md` inventory (from `csv/third_party_notices.csv`)
  now ships in the container images (under `/licenses`) and in every package
  tarball, and each package's `files[]` includes the AGPL `LICENSE` and the NOTICE,
  so a consuming vertical receives both. The API/worker images carry OCI
  `org.opencontainers.image.licenses`/`.source`/`.revision` labels, and a
  fail-closed license-compliance gate scans the image SBOM's dependency closure and
  fails on a disallowed or unknown license. The NOTICE is drift- and
  coverage-gated (every shipping NuGet dependency must be attributed). No package
  runtime surface changed.
- A typed live realtime client and hub connection contract (CORE-RT-007).
  `@livecore/contracts` exports the live SignalR hub path, the `SessionEvent`
  client-method name and the connection-parameter shape as stable constants/types
  (`RealtimeHubPaths`, `SESSION_EVENT_CLIENT_METHOD`, `SessionHubConnectionParams`,
  `LiveSessionEvent`), and `@livecore/sdk-ts` exposes a typed live client
  (`client.realtime.connect`) that joins only server-managed groups via identifiers
  (never group names), delivers `SessionEventReplayItem`-shaped envelopes through one
  handler shared with reconnect replay, and fails closed without an access token. The
  SDK stays free of a SignalR dependency via an injectable `hubConnectionFactory`. See
  the package changelogs for detail.
- The typed SDK now covers every implemented v1 route (CORE-SDK-006).
  `@livecore/sdk-ts` exposes a client method for every route in
  `csv/api_routes.csv` (provider-facing store-notification webhooks excepted) —
  adding the previously-missing `identity`, `organizations`, `audit`, `templates`
  and `recaps` resource groups and the missing lifecycle/delete methods across the
  existing groups — and `@livecore/contracts` gains the curated request/response
  DTOs those methods are typed against. See the package changelogs for detail.
- `@livecore/contracts` is now OpenAPI-derived (CORE-OAS-002): its `src/openapi.ts`
  types are generated from the committed OpenAPI 3 document
  (`openapi/livecore-v1.json`, CORE-OAS-001) with `openapi-typescript` and exposed
  under the `OpenApi` namespace. A CI drift gate in the `typescript` job regenerates
  the types and fails on any diff, and the curated request DTOs are validated against
  the generated schemas, so the server's contract and the published types cannot
  silently diverge. See the package changelog for detail.

### Changed

- All four packages are now **publishable** to the public npm registry under the
  `@livecore` scope, instead of being workspace-only `private` packages
  (CORE-PUB-001). Each manifest drops `private` and declares a complete published
  surface — `publishConfig` (public access + registry), `repository`,
  `sideEffects: false`, a conditional `exports` map and a `module` entry alongside
  `main`/`types` — with `files` shipping only `dist`, the per-package `CHANGELOG.md`,
  the AGPL `LICENSE` and the `THIRD-PARTY-NOTICES.md`, so `pnpm pack` produces a
  complete importable tarball and nothing internal/test/source-only leaks in. The
  `@livecore/sdk-ts → @livecore/contracts` link stays `workspace:*` for local
  development (rewritten to the resolved version at publish time), the lockstep
  VERSION discipline is unchanged, and the typed surface consumers import is
  unchanged. The registry decision is recorded in `docs/23_PACKAGE_VERSIONING.md`
  ("Publishing"); the release-gated CI publish job is a follow-up (CORE-PUB-002).

## [0.1.0] - 2026-06-13

First stable, documented release of the typed Core packages a vertical app
consumes. Each package now exports a `VERSION` runtime constant alongside its
existing `PACKAGE_NAME`, kept in lockstep with `package.json` and the package
`CHANGELOG.md` by a package-build test (CORE-SDK-005).

### Added

- `@livecore/contracts` — the stable, product-neutral contract types (DTOs,
  enums, events, transport constants, Problem Details) for the implemented
  `/api/v1` routes (CORE-SDK-001).
- `@livecore/sdk-ts` — the typed, OIDC-first Core API client over those
  contracts, with per-resource clients and a typed `LiveCoreApiError`
  (CORE-SDK-002).
- `@livecore/design-tokens` — the generic design-token contract, the neutral
  `baseTheme` and the `defineTheme` authoring helper (CORE-SDK-003).
- `@livecore/ui-core` — the generic UI primitive contract: the variant
  vocabularies, the primitive prop shapes and the `resolveVariant` helper
  (CORE-SDK-004).
- The package versioning and changelog process: Semantic Versioning, lockstep
  releases, per-package and root changelogs, the `VERSION` runtime export and
  the package-build tests that enforce version/changelog consistency
  (CORE-SDK-005).
