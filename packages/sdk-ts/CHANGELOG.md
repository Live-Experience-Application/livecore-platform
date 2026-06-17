# Changelog

All notable changes to `@livecore/sdk-ts` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [Unreleased]

### Added

- Full route coverage (CORE-SDK-006): the typed client now exposes a method for
  EVERY implemented `/api/v1` route in `csv/api_routes.csv` (the provider-facing
  store-notification webhooks excepted), so a vertical no longer hand-writes `fetch`
  for the majority of the API. Five new resource groups are added to `LiveCoreClient`:
  `identity` (`getCurrentPrincipal`, `GET /me`), `organizations` (`list`, `create`,
  `delete`, `removeMember`, `eraseMemberPersonalData`, `exportMemberPersonalData` —
  tenant offboarding plus the GDPR erasure and data-subject access/portability
  export), `audit` (`list`, the tenant's append-only audit log page), `templates`
  (`list`, `get`, `create`, `delete`) and `recaps` (`getSessionRecap`, role-projected).
  Existing groups gain the previously-missing methods: `workspaces.archive`/
  `listInvitations`/`acceptInvitation`/`revokeInvitation`/`removeMember`;
  `sessions.list`/`create`/`get`/`joinParticipant`/`leaveParticipant`;
  `scenes.get`/`reorder`/`delete`; `content.listBlocks`/`getBlock`/`deleteBlock`;
  `entities.delete`/`deleteRelationship`; `visibility.hide` (idempotent, mirroring
  `reveal`, with a new `HideOptions`); `assets.deleteLink`/`delete`; and
  `entitlements.getMyEntitlements`. Role-projected reads return the host-vs-participant
  union, and a `204 No Content` delete resolves to `void`. Every method is typed in
  terms of `@livecore/contracts` and routed through the existing fail-closed,
  bearer-authenticated transport; a server denial still surfaces as a typed
  `LiveCoreApiError`.
- Rate-limit signals on the error type (CORE-DX-005): `LiveCoreApiError` now
  surfaces `retryAfter` (seconds, from the `Retry-After` header) and `rateLimit`
  (`RateLimitInfo { limit, remaining, reset }`, from the `RateLimit-*` headers)
  parsed from a throttled response instead of discarding the response headers, so a
  caller can honor the server's back-off. A non-integer `Retry-After` (HTTP-date)
  is treated as absent rather than guessed; the new `ApiErrorDetails` and
  `RateLimitInfo` types are exported. The signals leak no tenant/principal detail
  (threat T7).
- Optimistic-concurrency round-trip over HTTP (CORE-DX-002): `WorkspacesClient.getWithETag`
  returns the workspace together with its weak `ETag` (`SdkResponse<WorkspaceResponse>`),
  and `WorkspacesClient.update` accepts a `ConditionalWriteOptions { ifMatch }` so a rename
  is made conditional on the version last read (a stale value is refused with `412`). The
  transport gained `HttpClient.sendWithETag` and a `RequestSpec.ifMatch` that sets the
  `If-Match` header; `SdkResponse` and `ConditionalWriteOptions` are exported.
- `EntityTypesClient` (exposed as `client.entityTypes`): `list`, `get` and
  `create` for a workspace's generic entity types under
  `/api/v1/workspaces/{workspaceId}/entity-types` (CORE-ENT-007). An entity type
  is an authoring/schema artifact rather than audience content, so all three
  methods return the single `EntityTypeResponse` shape (no role-projected union);
  the routes are authorized to the authoring roles only.
- `EntitiesClient` (exposed as `client.entities`): `list`, `get` and `create`
  for a workspace's generic entities under
  `/api/v1/workspaces/{workspaceId}/entities` (CORE-ENT-006). `list`/`get` return
  the role-projected union (`EntityResponse[] | ParticipantEntityResponse[]` and
  `EntityResponse | ParticipantEntityResponse`); `create` returns the full host
  `EntityResponse`.

### Changed

- `RealtimeClient.getSessionEvents` reconnect cursor is now the per-session
  monotonic sequence number: `SessionEventReplayParams.afterEventId` (a UUID) is
  replaced by `afterSequence` (a number), sent as the `?afterSequence=` query
  parameter. A cursor of N replays N+1.. with no skips or duplicates
  (CORE-RTC-001).

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
