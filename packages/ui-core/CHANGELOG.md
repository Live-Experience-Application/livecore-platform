# Changelog

All notable changes to `@livecore/ui-core` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [Unreleased]

## [0.5.0] - 2026-06-26

Released in lockstep with the other `@livecore/*` packages, which always share one
version. There are no changes to the `@livecore/ui-core` typed surface in this
release.

## [0.4.0] - 2026-06-23

Released in lockstep with the other `@livecore/*` packages, which always share one
version. There are no changes to the `@livecore/ui-core` typed surface in this
release.

## [0.3.0] - 2026-06-21

Released in lockstep with the other `@livecore/*` packages, which always share one
version. There are no changes to the `@livecore/ui-core` typed surface in this
release.

## [0.2.0] - 2026-06-19

### Changed

- The package is now **publishable** to the public npm registry instead of being a
  workspace-only `private` package (CORE-PUB-001). `private` is removed and the
  manifest declares the published surface — `publishConfig` (public access +
  registry), `repository`, `sideEffects: false`, a conditional `exports` map and a
  `module` entry alongside `main`/`types` — while `files` still ships only `dist`,
  the `CHANGELOG.md`, the AGPL `LICENSE` and the `THIRD-PARTY-NOTICES.md`. The typed
  surface a consumer imports is unchanged. See `docs/23_PACKAGE_VERSIONING.md`
  ("Publishing").
- The manifest now declares `engines` (`node >= 22`) and `repository.directory`
  (`packages/ui-core`), completing the publish-shape, and the release publish runs
  with npm build provenance (`--provenance` under a job-scoped `id-token: write`), so
  each published version carries a verified provenance attestation linking the tarball
  to this pipeline (CORE-PUB-004). Manifest metadata and publish process only — the
  typed surface a consumer imports is unchanged. See
  `docs/23_PACKAGE_VERSIONING.md` ("npm build provenance").

## [0.1.0] - 2026-06-13

First stable, documented release of the generic, product-neutral UI primitive
contract that vertical apps build their components on.

### Added

- The variant vocabularies a primitive's props are drawn from (the semantic
  `tone`, the `size` and `emphasis` scales, the surface level and the layout
  options), exported as `as const` tuples alongside their string-literal unions.
- The typed prop shape of each generic primitive (`Surface`, `Stack`, `Text`,
  `Heading`, `Button`, `Badge`, `Field`, `Spinner`, `Divider`, `Avatar`).
- The pure `resolveVariant` helper and its `DEFAULT_VARIANT`, which fill a
  partially-specified variant with Core's stable defaults.
- The `PACKAGE_NAME` and `VERSION` runtime constants so a consumer can
  introspect which Core package release it is running against (CORE-SDK-005).
