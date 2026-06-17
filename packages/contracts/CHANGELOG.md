# Changelog

All notable changes to `@livecore/contracts` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [Unreleased]

### Added

- Browser-consumer response header names (CORE-DX-005): `ResponseHeaders` gains
  `Location`, `RetryAfter` (`Retry-After`), `RequestId` (`X-Request-Id`) and the
  rate-limit family `RateLimitLimit`/`RateLimitRemaining`/`RateLimitReset`
  (`RateLimit-Limit`/`-Remaining`/`-Reset`). These are the headers the Core API
  CORS policy exposes (`Access-Control-Expose-Headers`) so a cross-origin browser
  SDK can read the rate-limit, correlation and created-resource signals; none
  carries tenant/principal content (threat T7).
- The optimistic-concurrency token is surfaced over HTTP (CORE-DX-002):
  `WorkspaceResponse.version` carries the resource's version (the `xmin` token),
  the matching single-resource response sets a weak `ETag` header, and a consumer
  echoes it back as the new `If-Match` request header (`RequestHeaders.IfMatch`,
  `ResponseHeaders.ETag`) to make a rename/archive conditional. A stale value is
  refused before the write with `412` (`precondition_failed`, added to the
  `ProblemCodes` catalog and `CoreErrorStatusCodes`), so a GET-then-PUT across HTTP
  cannot silently clobber a concurrent change.
- The stable Problem Details error-code catalog: a `ProblemCodes` runtime
  `as const` tuple and its `ProblemCode` string-literal union, plus an optional
  `ProblemDetails.code` field. Every Core API error now carries a stable,
  machine-readable `code` drawn from this catalog, so a consumer branches on the
  code rather than the human `title`/`detail` prose. Distinct conditions that
  share an HTTP status remain distinct codes — notably the three `409`s
  (`quota_exceeded`, `workspace_archived`, `concurrency_conflict`). The catalog
  mirrors the server-side catalog exactly, asserted by a contract test
  (CORE-DX-001).
- Entity-type module contracts (`CreateEntityTypeRequest`, `EntityTypeResponse`)
  for the entity-type create/list/by-id-read routes under
  `/api/v1/workspaces/{workspaceId}/entity-types`. An entity type is the
  data-driven definition of a kind of entity (template key plus field/type
  metadata, the template boundary); it is an authoring/schema artifact rather than
  audience content, so there is a single `EntityTypeResponse` shape (no
  host-vs-participant projection) (CORE-ENT-007).
- Entities module contracts (`CreateEntityRequest`, `EntityResponse`,
  `ParticipantEntityResponse`) for the entity create/list/by-id-read routes under
  `/api/v1/workspaces/{workspaceId}/entities`. An entity is content, so the list
  and read are projected by role: the host-content roles receive the full
  `EntityResponse` (including its attribute-values), every other role the stripped
  `ParticipantEntityResponse` (CORE-ENT-006).
- `SessionEventReplayItem.sequence`: the per-session, gap-free, strictly
  monotonic event sequence number a client orders the stream by and uses to
  detect a missed event as a gap (CORE-RTC-001).

### Changed

- The reconnect-replay cursor is now the per-session `sequence` number rather
  than the event id; `SessionEventReplayResponse.events` are in append (sequence)
  order.

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
