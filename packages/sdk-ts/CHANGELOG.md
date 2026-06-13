# Changelog

All notable changes to `@livecore/sdk-ts` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [0.1.0] - 2026-06-13

First stable, documented release of the typed Core API client that vertical
apps consume.

### Added

- `LiveCoreClient`, the typed client that wraps the implemented `/api/v1`
  routes with methods returning the exact `@livecore/contracts` response types,
  grouped into resource clients (`workspaces`, `sessions`, `scenes`, `content`,
  `visibility`, `realtime`, `assets`, `entitlements`, `store`).
- OIDC-first transport: the caller supplies an access-token provider, the client
  fails closed when no token is available, and a non-success response surfaces as
  a typed `LiveCoreApiError` carrying the HTTP status and Problem Details (never
  the token or request body).
- An injectable `fetch` transport, so no runtime dependency beyond
  `@livecore/contracts` is added.
- The `PACKAGE_NAME` and `VERSION` runtime constants so a consumer can
  introspect which Core package release it is running against (CORE-SDK-005).
