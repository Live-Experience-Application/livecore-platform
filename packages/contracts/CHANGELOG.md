# Changelog

All notable changes to `@livecore/contracts` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [0.1.0] - 2026-06-13

First stable, documented release of the typed Core contract surface that
vertical apps consume.

### Added

- Request/response DTOs for the implemented `/api/v1` routes (workspaces,
  sessions, scenes, content blocks, visibility/reveal, realtime events, assets,
  entitlements and store).
- Generic enumerations (membership roles, lifecycle statuses, resource and
  content kinds, quota/store/ad-eligibility codes) exported as both
  string-literal unions and runtime `as const` tuples.
- The RFC 7807 `ProblemDetails` error shape, the transport constants
  (`API_BASE_PATH`, request header names) and the realtime session event
  vocabulary.
- The `PACKAGE_NAME` and `VERSION` runtime constants so a consumer can
  introspect which Core package release it is running against (CORE-SDK-005).
