# Changelog

All notable changes to `@livecore/design-tokens` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

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
