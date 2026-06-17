# Package Versioning and Changelog Process

This document defines how the LiveCore Core Platform versions and changes its
published TypeScript packages so that vertical apps can consume **stable, typed
Core packages** with predictable upgrade semantics (the acceptance criterion of
the `SDK and UI Core Packages` epic; CORE-SDK-005).

It applies only to the published TypeScript packages:

```text
@livecore/contracts       packages/contracts
@livecore/sdk-ts          packages/sdk-ts
@livecore/design-tokens   packages/design-tokens
@livecore/ui-core         packages/ui-core
```

The .NET API and worker hosts are deployed applications, not published packages,
and are versioned by deployment — they are out of scope here.

## Semantic Versioning

The packages follow [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
Given a version `MAJOR.MINOR.PATCH`:

- **MAJOR** — a breaking change to a package's public, typed surface: a removed
  or renamed export, a narrowed accepted type, a widened required input, a
  changed enum/tuple value, or any change that can make a previously compiling
  consumer stop compiling or change runtime behavior.
- **MINOR** — a backward-compatible addition: a new export, a new optional field,
  a new enum member added to a tuple/union, a new resource client method.
- **PATCH** — a backward-compatible fix that changes neither the types nor the
  documented behavior (documentation, internal refactor, build fix).

While the packages are pre-1.0 (`0.y.z`), the public surface may still evolve;
a breaking change is released as a **minor** bump and is always called out in the
changelog under a `### Changed` or `### Removed` heading. Consumers should pin a
caret range (for example `^0.1.0`) and read the changelog before upgrading.

### Additive-only evolution and the runtime API contract (CORE-DX-006)

The MINOR-vs-MAJOR rule above is the package-surface side of one repository-wide
policy: the Core evolves **additive-only** between breaking versions. A new optional
field, a new export/endpoint or a new enum/event member is additive (a MINOR change to
the packages; a same-`/api/v1` change to the runtime API). Removing, renaming or
narrowing an existing field/value, or widening a required input, is breaking — a MAJOR
change to the packages and a new runtime API version, never an in-place edit of `v1`.
The runtime side adds an **advance signal**: a retiring route or field emits the RFC 8594
`Sunset` and `Deprecation` headers so a vertical learns the retirement date before the
contract changes. The runtime mechanism, the header format and the CORS exposure are
documented in `docs/02_ARCHITECTURE.md` ("Evolution, deprecation and sunset") and
`docs/08_API_CONTRACTS.md` ("API evolution"). A `### Deprecated` changelog heading (Keep
a Changelog, below) is the package-surface counterpart of the `Deprecation` header.

### Generated OpenAPI-derived contracts (CORE-OAS-002)

`@livecore/contracts` is **OpenAPI-derived**: its `src/openapi.ts` is generated from
the committed OpenAPI 3 document `openapi/livecore-v1.json` (itself generated from the
running route table and drift-gated against it, CORE-OAS-001) by
`pnpm --filter @livecore/contracts run generate` (`openapi-typescript`), and re-exported
under the `OpenApi` namespace. A CI drift gate in the `typescript` job
(`check:openapi`, mirrored by the package build test) regenerates the types and fails
on any diff, so the server's contract and the published types can never silently
diverge. The curated, human-facing request/response DTOs in the package are validated
against the generated schemas by the type-tests (the generator marks every required
reference-type field `nullable`, an ASP.NET minimal-API quirk, so the curated DTOs stay
the primary documented surface).

Generation does **not** change the versioning rules — it enforces them. When an
intentional server change regenerates the types, classify the diff with the same
MAJOR-vs-MINOR rule above: a new optional field, schema or endpoint is a **MINOR**
addition; a removed, renamed or narrowed field, or a widened required input, is a
**breaking** change released as a SemVer event and called out under a `### Changed` or
`### Removed` changelog heading. A regenerated contract change is therefore a normal
changelog entry, never a silent edit: the drift gate forces the regeneration into the
same commit, and this document's rules decide the version bump.

### Drift-gated session-event contract (CORE-RT-008)

The OpenAPI document covers the HTTP route DTOs, not the realtime hub's event-type
names or payloads, so the published session-event surface gets its own drift gate.
`@livecore/contracts` exports `KnownSessionEventTypes` (the names a consumer can
switch on) and, for each, a typed identifier-only payload in `SessionEventPayloadMap`
(with the `ParsedSessionEvent` discriminated union and the runtime
`KnownSessionEventPayloadFields` map). A CI gate in the `typescript` job
(`check:events`, mirrored by the package test) fails if that vocabulary or any
payload field set diverges from the server's own sources — the non-deferred
`csv/event_catalog.csv` events, the `apps/api/Realtime/SessionEventTypes.cs`
constants and the `apps/api/Realtime/SessionEventPayloads.cs` payload records. This
is the TypeScript-side mirror of spec-consistency check 11 (which binds the C#
constants to the catalog), so a new, removed or renamed event or payload field
forces the published contract to be updated in the same commit.

Like the OpenAPI gate, this does not change the versioning rules — it enforces
them. Classify a forced contract update with the same MAJOR-vs-MINOR rule: a new
event or a new optional payload field is a **MINOR** addition; a removed or renamed
event or payload field is a **breaking** change called out under `### Changed` /
`### Removed`. The session-event `eventType` and `payload` wire fields stay
forward-compatible (a plain `string` and a raw JSON string), so a vertical never
rejects a future event a newer server emits.

### Live realtime hub contract — a non-route surface (CORE-RT-007)

The realtime hub is **not** an `/api/v1` REST route, so its contract is neither in the
OpenAPI document (CORE-OAS-002) nor in `csv/api_routes.csv`. `@livecore/contracts`
mirrors the server's live SignalR constants by hand — `RealtimeHubPaths.session`
(`/hubs/session`), `SESSION_EVENT_CLIENT_METHOD` (`SessionEvent`),
`REALTIME_ACCESS_TOKEN_QUERY_PARAM` (`access_token`) and the
`SessionHubConnectionParams` shape — and `@livecore/sdk-ts` exposes the typed live
client (`client.realtime.connect`). Each addition is a **MINOR** change (new exports, a
new resource-client method) classified by the same rule above; the hub path and the
client-method name are pinned by the package build/type tests so a server-side rename
surfaces as a contract change rather than a silent break.

Because the hub is not a route, the SDK↔`csv/api_routes.csv` coverage parity gate
(CORE-SDK-007, below) **excepts** the live client method: `connect` has no route row, so a
parity check that demanded one per method would fail on it. The drift discipline for the
hub is the pinned constants here, not a route-coverage gate.

### Drift-gated SDK↔route coverage parity (CORE-SDK-007)

The typed SDK is the consumability surface a vertical builds on, but nothing bound it to
the API: the published coverage regressed to roughly 45% silently because the SDK could
fall behind a new route without any gate noticing. `@livecore/sdk-ts` exposes one typed
client method per implemented `/api/v1` route (CORE-SDK-006); a CI gate in the `typescript`
job (`check:sdk-parity`, mirrored by the package test) now **enforces** that parity so it
cannot regress again. The gate asserts, in BOTH directions over the route set, that the SDK
exposes exactly one client method per **non-callback** `csv/api_routes.csv` route:

- a route documented in `csv/api_routes.csv` with no client method fails (the SDK fell
  behind a new route), and
- a client method whose route is not a documented non-callback route fails (a phantom or
  renamed route, or a forbidden provider callback).

**Provider-facing webhook callbacks are explicitly excepted.** The routes whose roles cell
is `none (provider callback)` — today the Apple/Google store-notification receivers — are
called by the store servers, not by a vertical app, so the SDK must NOT expose a method for
them; a method that did would fail the gate. The **live realtime client** (`connect`) is
excepted for free: it opens the `/hubs/session` hub rather than issuing an `/api/v1`
request, so it has no route call-site and contributes no route key (see the live hub
contract above).

This is the TypeScript-side mirror of spec-consistency check 6 (which binds
`csv/api_routes.csv` to the implemented minimal-API routes,
`scripts/LiveCoreSpecConsistency.psm1`): there the C# route registrations are bound to the
CSV; here the published SDK surface is bound to it, reading `csv/api_routes.csv` as the
single source of truth and the resource client SOURCES for the methods (so the gate needs
no build). Like the gates above, it does not change the versioning rules — it enforces
them: a new route plus its SDK method is a **MINOR** addition (a new resource-client
method), classified by the same rule above, and the gate forces the SDK method into the
same commit as the route.

## Lockstep releases

The four packages are released **together** and always share a single version.
They are designed as one coherent surface (`@livecore/sdk-ts` is typed entirely
in terms of `@livecore/contracts`; `@livecore/ui-core` props are themed by
`@livecore/design-tokens`), so a single shared version removes any ambiguity
about which combination a vertical is running. A release bumps the `version`
field of all four `package.json` files and the `VERSION` runtime constant in each
package's `src/index.ts` to the same value, even for a package with no change in
that release.

## Runtime version introspection

Each package exports two stable runtime values from its entry point:

```ts
import { PACKAGE_NAME, VERSION } from "@livecore/contracts";
```

`VERSION` lets a vertical app log or assert exactly which Core package release it
is running against. The exported `VERSION` literal is the single source of truth
inside the bundle and is kept in lockstep with the package manifest and the
changelog (see below).

## Changelog

Every published package keeps a `CHANGELOG.md` in
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format, and the
repository root keeps a workspace-level `CHANGELOG.md` summarizing the release
across all four packages. Each release adds a `## [MAJOR.MINOR.PATCH] - YYYY-MM-DD`
section whose `Added` / `Changed` / `Deprecated` / `Removed` / `Fixed` subsections
describe the change in terms of the package's public surface only — never any
vertical domain language (`AGENTS.md`).

## How to cut a release

1. Pick the new version from the nature of the changes (the rules above).
2. Update the `version` field in all four `packages/*/package.json` files.
3. Update the `VERSION` constant in each `packages/*/src/index.ts` to match.
4. Add a `## [<version>] - <date>` entry to each affected package's
   `CHANGELOG.md` and to the root `CHANGELOG.md`.
5. Run the full local CI-equivalent (`README.md`). The package-build tests fail
   if `VERSION`, `package.json` and the changelog top entry disagree, so version
   drift cannot ship.

## Enforcement

The consistency of the process is enforced by tests, not convention:

- A **type test** in each package checks at compile time that `VERSION` is a
  well-formed, non-widened Semantic Versioning string literal.
- A **package-build test** in each package checks, against the compiled output,
  that `VERSION` is valid SemVer, that it equals the package's own
  `package.json` version, that `CHANGELOG.md` has a top entry for that version,
  and that the changelog ships with the package (`files` includes `CHANGELOG.md`).
  It also asserts (CORE-LIC-003) that every package ships the AGPL `LICENSE` and
  the generated `THIRD-PARTY-NOTICES.md` — both listed in `files[]`, with the
  `LICENSE` byte-identical to the repository-root AGPL text — so a consuming
  vertical receives the license and the third-party attribution inventory in the
  tarball. The `THIRD-PARTY-NOTICES.md` is generated and drift-gated; see
  `docs/16_LICENSING.md` ("Third-party attribution and the license-compliance
  gate").
- A **cross-package version lockstep test** (CORE-CMP-003,
  `tests/version-lockstep/version-lockstep.test.mjs`, run by `pnpm run test:versions`
  and the CI `typescript` job) checks that the four packages agree on **one**
  shared version. The per-package tests above each validate only their **own**
  triple, so without this test bumping one package to `0.2.0` while the others
  stayed at `0.1.0` still passed; the lockstep test asserts every package's
  `package.json` version, exported `VERSION` and `CHANGELOG.md` top entry — and
  the root `CHANGELOG.md` — all match the same value, so a one-package bump fails
  CI rather than shipping.

## Release-tag and package-version consistency

The .NET API and worker host **images** are versioned by the **release tag**
(`v<MAJOR>.<MINOR>.<PATCH>`, `scripts/LiveCoreImageTags.psm1`), not by a manifest
of their own — they are deployed applications, not published packages. So the
hosts are versioned separately from the packages by mechanism, and the intended
relationship at release time is that the release tag equals the packages' shared
version: one coherent repository release where the published image tag and the
package version are the same number.

A publish gate enforces this so drift cannot ship (CORE-CMP-003):

- `scripts/LiveCoreReleaseVersion.psm1` reads the four packages' shared version
  (fail-closed if the four disagree) and compares it to the release tag's version.
- `scripts/assert-release-version.ps1` is the gate the CI `publish` job runs on a
  release tag **before** building or pushing any image: it exits non-zero — and
  the publish fails — when the release tag's version does not equal the shared
  package version.
- `scripts/test-release-version.ps1` tests the gate logic (a matching tag passes;
  a mismatching tag, a non-release ref, or the four packages out of lockstep fail
  closed) and runs on every push and pull request via the CI `publish-dry-run`
  job, so the gate itself is proven without a registry push.

Cutting a release therefore means the version in step 2–4 of "How to cut a
release" above and the `v<version>` tag you push are the **same** value.

## Publishing

The packages are currently marked `private` and consumed inside this workspace.
Wiring an actual publish pipeline (a registry, release automation and removing
`private`) is a follow-up; this document defines the versioning and changelog
discipline that any such pipeline builds on.
