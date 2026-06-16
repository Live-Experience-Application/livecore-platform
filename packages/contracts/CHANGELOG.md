# Changelog

All notable changes to `@livecore/contracts` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [Unreleased]

### Added

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
