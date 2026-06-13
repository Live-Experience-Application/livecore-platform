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
