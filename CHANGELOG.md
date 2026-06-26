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

- `@livecore/contracts`: the async export-request shapes `CreateExportRequest` and
  `ExportJobResponse` and the `ExportJobStatus` enum (CORE-EXP-003), for the new
  `POST /api/v1/workspaces/{workspaceId}/exports` route that mints a workspace export job.
- `@livecore/sdk-ts`: the `client.exports.createExport` method (CORE-EXP-003), which requests an
  async workspace export (with an optional idempotency key) so a vertical can drive the
  request-then-read export lifecycle end to end. A MINOR change.
- `@livecore/contracts`: the entity-relationship shapes `CreateEntityRelationshipRequest`
  and `EntityRelationshipResponse` (CORE-ENT-008), for the new entity-relationship create
  and list routes.
- `@livecore/sdk-ts`: the `client.entities.createRelationship` and
  `client.entities.listRelationships` methods (CORE-ENT-008), making the entity-relationship
  graph authorable and readable (a directed edge between two entities), not only deletable.
  A MINOR change.

## [0.4.0] - 2026-06-23

This release adds the workspace member-administration reads and command, the
host-side asset enumeration reads, retry-safe (idempotent) resource creates and
the participant current-scene projection built since `0.3.0`, bumped in lockstep
across all four `@livecore/*` packages. Everything is additive — new contract
types and an optional response field, new SDK resource methods and a new optional
idempotency-key option on the create commands — so it ships as a MINOR bump
(`docs/23_PACKAGE_VERSIONING.md`). `@livecore/design-tokens` and `@livecore/ui-core`
carry no surface change this release and are bumped only to keep the four packages
on one version. The operator publishes it by pushing the matching `v0.4.0` git tag,
which triggers the tag-gated CI publish pipeline (`publish` and `publish-packages`);
the gates assert the tag equals this shared package version before anything ships.

### Added

- `@livecore/contracts`: the host workspace member **roster** entry
  `WorkspaceMemberRosterEntryResponse` for `GET /api/v1/workspaces/{workspaceId}/members`
  (CORE-WSM-001); the member **role-change** request `UpdateWorkspaceMemberRoleRequest`
  for `PATCH /api/v1/workspaces/{workspaceId}/members/{memberId}` (CORE-WSM-002); the
  host **asset** projection `AssetResponse` for the workspace asset enumeration
  `GET /api/v1/workspaces/{workspaceId}/assets` (CORE-ALC-003) and the per-resource
  attachments read `GET /api/v1/assets/by-target/{targetType}/{targetId}` (CORE-ALC-004);
  and the optional audience-safe `ParticipantVisibleFeedResponse.currentScene`
  (`ParticipantSceneResponse | null`) current-scene projection (CORE-APROJ-005).
- `@livecore/sdk-ts`: the new client methods `client.workspaces.listMembers`
  (CORE-WSM-001), `client.workspaces.updateMemberRole` (CORE-WSM-002, the SDK's first
  `PATCH` route), `client.assets.list` (CORE-ALC-003) and `client.assets.listForResource`
  (CORE-ALC-004); and the shared optional `IdempotentCreateOptions` (`idempotencyKey`),
  now accepted by the retry-safe resource-create commands `client.workspaces.create`,
  `client.sessions.create`, `client.scenes.create`, `client.content.createBlock` and
  `client.assets.createLink` (CORE-DX-008), and `client.entities.create` (CORE-DX-009).

## [0.3.0] - 2026-06-21

This release adds the audience-projection, participant self-service,
invitation-discovery, asset-lifecycle, sealed/scheduled-visibility and closed-app
Web Push surfaces built since `0.2.0`, bumped in lockstep across all four
`@livecore/*` packages. Everything is additive — new optional fields, new exports,
new SDK resource methods and new design-token roles, plus the participant
visible-feed item changing from an empty placeholder to a populated, audience-safe
shape — so it ships as a MINOR bump (`docs/23_PACKAGE_VERSIONING.md`). The operator
publishes it by pushing the matching `v0.3.0` git tag, which triggers the tag-gated
CI publish pipeline (`publish` and `publish-packages`); the gates assert the tag
equals this shared package version before anything ships.

### Added

- `@livecore/contracts`: the populated, audience-safe participant **visible feed**
  (`ParticipantVisibleFeedItem` now carries the resource identity, an audience-safe
  `title`/`body`, `revealedAt`, a `revealScope` marker, `locked`, `scheduledRevealAt`
  and an `attachments` list of `ParticipantVisibleFeedAttachment` — CORE-APROJ-001/002,
  CORE-ALC-002, with the new `FeedRevealScope` enum); `ParticipantEntityResponse.entityTypeKey`
  (CORE-APROJ-003); `VisibilityRuleResponse.resourceLabel`/`locked`/`scheduledRevealAt`
  and `CreateVisibilityRuleRequest.scheduledRevealAt` (CORE-APROJ-004, CORE-VSEAL-001/002);
  `SessionParticipantContext` for `GET /api/v1/sessions/{sessionId}/me` and
  `ParticipantRosterParticipant.isSelf` (CORE-PSELF-001); `MyPendingWorkspaceInvitationResponse`
  for `GET /api/v1/me/invitations` (CORE-INV-002); the closed-app Web Push contracts in a
  new `push.ts` module (CORE-PUSH-001); the asset confirm-upload contracts
  (`ConfirmUploadRequest`/`ConfirmUploadResponse`, CORE-ALC-001); and the subject's push
  subscriptions in the personal-data export (CORE-PUSH-001).
- `@livecore/sdk-ts`: the echoed `requestId`/`traceparent` correlation ids on the
  success `SdkResponse` envelope and on `LiveCoreApiError` (CORE-SDX-001); the new
  resource groups `client.invitations` (CORE-INV-002) and `client.pushSubscriptions`
  (CORE-PUSH-001); and the new methods `client.assets.confirmUpload` (CORE-ALC-001),
  `client.realtime.getSessionParticipantContext` (CORE-PSELF-001) and
  `client.visibility.lockRule`/`unlockRule` (CORE-VSEAL-001).
- `@livecore/design-tokens`: paired `*Foreground` on-status color roles
  (`successForeground`, `warningForeground`, `dangerForeground`, `infoForeground`)
  across the role tuple and the light/dark base theme, each clearing the WCAG 2.1 AA
  body-text contrast threshold (CORE-DTOK-001).

### Changed

- `@livecore/contracts` `ParticipantVisibleFeedItem` changed from the empty
  `Record<string, never>` placeholder to the populated, audience-safe interface above
  (CORE-APROJ-001 first realigned the published type to the server DTO it had drifted
  from; CORE-APROJ-002 and CORE-ALC-002 enriched it). The feed is no longer a
  perpetually-empty skeleton. Pre-1.0 the shape change ships as a MINOR bump; a
  consumer that relied on the empty type reads the new fields.
- `@livecore/ui-core` has no surface change this release; it is bumped in lockstep so
  the four packages always share one version.

## [0.2.0] - 2026-06-19

This release cuts the large body of work merged since `0.1.0` into one dated,
lockstepped version across all four `@livecore/*` packages (CORE-REL-001). The
operator publishes it by pushing the matching `v0.2.0` git tag, which triggers
the tag-gated CI publish pipeline (`publish` and `publish-packages`); the gates
assert the tag equals this shared package version before anything ships
(`docs/23_PACKAGE_VERSIONING.md`, "How to cut a release").

### Added

- A single API/SDK **stability policy and path to 1.0** (CORE-REL-002), documented
  in `docs/23_PACKAGE_VERSIONING.md` so an adopter reads one clear policy before
  building on the Core. It states the **public surface** the commitment covers (the
  four `@livecore/*` packages plus the `/api/v1` runtime contract), the **pre-1.0
  posture** (a breaking change is a MINOR bump while `0.y.z`; pin a caret range and
  read the changelog), a **concrete deprecation window** of at least **180 days (six
  months)** of RFC 8594 `Sunset`/`Deprecation` advance notice, and **what declaring
  1.0 means** (full SemVer on that surface). The window is enforced, not just written
  down: the server's `DeprecationNotice` refuses to construct a deprecation whose
  deprecation-to-sunset gap is shorter than the documented 180 days, and a test pins
  the code constant to the documented value. No published package surface changed.
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
- The publish-shape is completed and the release publish carries npm build provenance
  (CORE-PUB-004). Every `packages/*/package.json` now declares `engines`
  (`node >= 22`) and `repository.directory` (`packages/<name>`), and the
  `publish-packages` job publishes with `--provenance` under a job-scoped
  `id-token: write`, so each published `@livecore/*` version carries a verified
  provenance attestation linking the tarball to this pipeline (the npm-side analogue
  of the attested container images). Manifest metadata and publish process only — the
  typed surface consumers import is unchanged. See `docs/23_PACKAGE_VERSIONING.md`
  ("npm build provenance").

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
