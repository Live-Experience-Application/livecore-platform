# Changelog

All notable changes to `@livecore/design-tokens` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [Unreleased]

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
  (`packages/design-tokens`), completing the publish-shape, and the release publish
  runs with npm build provenance (`--provenance` under a job-scoped `id-token: write`),
  so each published version carries a verified provenance attestation linking the
  tarball to this pipeline (CORE-PUB-004). Manifest metadata and publish process only —
  the typed surface a consumer imports is unchanged. See
  `docs/23_PACKAGE_VERSIONING.md` ("npm build provenance").

## [0.1.0] - 2026-06-13

First stable, documented release of the generic, product-neutral design-token
contract that vertical apps theme the Core UI with.

### Added

- The token contract — the categories (`color`, `spacing`, `typography`,
  `radius`, `shadow`, `breakpoint`, `motion`) and the stable generic keys within
  each — exported as `as const` tuples alongside their string-literal unions.
- A neutral `baseTheme` default that satisfies the contract, and the
  `defineTheme` authoring helper that makes the compiler check no required token
  is dropped when a vertical re-skins the Core UI.
- The `PACKAGE_NAME` and `VERSION` runtime constants so a consumer can
  introspect which Core package release it is running against (CORE-SDK-005).
