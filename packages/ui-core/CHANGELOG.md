# Changelog

All notable changes to `@livecore/ui-core` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

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
