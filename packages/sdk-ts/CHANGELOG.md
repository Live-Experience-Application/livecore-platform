# Changelog

All notable changes to `@livecore/sdk-ts` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [Unreleased]

## [0.4.0] - 2026-06-23

### Added

- The workspace member-roster read `client.workspaces.listMembers` (CORE-WSM-001): the
  bounded page of `WorkspaceMemberRosterEntryResponse` an Owner/Admin renders a members
  screen from, returning the membership `id` that `client.workspaces.removeMember` and
  `updateMemberRole` require (otherwise unobtainable). A non-administration caller (and a
  foreign/unknown workspace) is hidden as `404`. A new resource-client method (a MINOR
  change).
- The workspace member role-change command `client.workspaces.updateMemberRole`
  (CORE-WSM-002): the SDK's first `PATCH` route, changing a member's generic role so an
  administrator can correct it without remove-and-reinvite. The last remaining Owner cannot
  be demoted (a `409`); it accepts the optional `ConditionalWriteOptions.ifMatch` to make
  the change conditional on the version last read (a stale value is refused with `412`,
  CORE-DX-002) and returns the updated `WorkspaceMemberResponse` (its new version on the
  response `ETag`). A new resource-client method (a MINOR change).
- The host asset reads `client.assets.list` (CORE-ALC-003) — a workspace's host
  `AssetResponse` assets as a bounded page — and `client.assets.listForResource`
  (CORE-ALC-004) — the host assets linked to one target resource (a content block or
  entity), the target's `workspaceId` named alongside `organizationSlug`. Both are the host
  projection, distinct from the audience-safe participant feed attachments. Two new
  resource-client methods (a MINOR change).
- A shared optional idempotency-key option for the retry-safe resource-create commands,
  exported as `IdempotentCreateOptions` (CORE-DX-008). `client.workspaces.create`,
  `client.sessions.create`, `client.scenes.create`, `client.content.createBlock` and
  `client.assets.createLink` (CORE-DX-008), and `client.entities.create` (CORE-DX-009), now
  accept an optional trailing options argument carrying an optional `idempotencyKey`,
  forwarded as the `Idempotency-Key` request header so a retry under the SAME key replays
  the original resource the server already deduped (CORE-DX-004) instead of creating a
  duplicate. The option is optional, so omitting it preserves the prior unconditional
  create — an additive (a MINOR change); no route or wire-contract change.

## [0.3.0] - 2026-06-21

### Added

- The echoed correlation ids on the SDK (CORE-SDX-001): the Core API already echoes
  `X-Request-Id` and the W3C `traceparent` on every response (its
  `CorrelationHeaderMiddleware`, exposed via CORS), and the SDK now surfaces both
  instead of discarding them. `LiveCoreApiError` carries `readonly requestId` and
  `traceparent`, and the success `SdkResponse` envelope exposes the same two
  alongside `data` and `etag`, so a consumer can log `request_id` with every call and
  show it in an error state without wrapping the injected `fetch` transport. The ids
  are populated on both the success and the fail-closed error path, and are
  `undefined` only when Core sent none — never fabricated (a blank header is treated
  as absent). No new runtime dependency and no route/method change; the new optional
  fields are an additive change to the typed surface.
- A new `InvitationsClient` resource group, `client.invitations` (CORE-INV-002): the
  authenticated caller's own pending-invitation self-discovery read over
  `GET /api/v1/me/invitations`, so an onboarding flow can discover the workspaces that
  invited it and then drive `client.workspaces.acceptInvitation`. A new resource-client
  method (a MINOR change).
- A new `PushSubscriptionsClient` resource group, `client.pushSubscriptions`
  (CORE-PUSH-001): `getVapidPublicKey()`, `register(...)` and `delete(subscriptionId)`
  over the closed-app Web Push registration routes (`GET /api/v1/push/vapid-public-key`,
  `POST` and `DELETE /api/v1/me/push-subscriptions`). New resource-client methods (a
  MINOR change).
- `client.assets.confirmUpload(assetId, request)` (CORE-ALC-001): the `Pending` →
  `Available` confirm-upload transition over `POST /api/v1/assets/{assetId}/confirm-upload`.
  A new resource-client method (a MINOR change).
- `client.realtime.getSessionParticipantContext(...)` (CORE-PSELF-001): the caller's own
  session participant context over `GET /api/v1/sessions/{sessionId}/me`, so an audience
  surface can learn its own surrogate participant id and then call the participant-keyed
  reads for itself. A new resource-client method (a MINOR change).
- `client.visibility.lockRule(...)` and `client.visibility.unlockRule(...)`
  (CORE-VSEAL-001): seal and clear the authoring lock on a visibility rule over the
  `.../visibility-rules/{ruleId}/lock` and `/unlock` routes. New resource-client methods
  (a MINOR change).

## [0.2.0] - 2026-06-19

### Added

- A typed live realtime client over the SignalR session hub (CORE-RT-007):
  `client.realtime.connect(params, onEvent)` opens `/hubs/session` with the connection
  identifiers (`SessionHubConnectionParams` — `organizationSlug`, `sessionId` and, for
  a participant, its own `participantId`, never a group name), registers the single
  `SessionEvent` client method as ONE handler that delivers the same
  `SessionEventReplayItem`-shaped envelope (`LiveSessionEvent`) reconnect replay
  returns, and returns a `LiveSessionConnection` handle whose `stop()` closes the
  connection. It fails closed without an access token — the token is resolved BEFORE
  any connection is built or started, so an empty token rejects client-side with a
  `LiveCoreError` and no hub connection is opened (the token then refreshes on each
  reconnect via the connection's `accessTokenFactory`, the `access_token` query
  parameter, so the secret never sits in the composed URL). Because a client supplies
  only identifiers, it cannot select a group or another participant's feed — the
  server resolves the authorized server-managed groups (CORE-RT-002; threat T3). To
  stay free of a SignalR dependency, the SDK takes a new optional
  `hubConnectionFactory` option (the same injectable-transport seam the REST path uses
  for `fetch`; a vertical wires it to `@microsoft/signalr`); the
  `HubConnectionFactory`, `HubConnectionLike`, `HubConnectionRequest` and
  `LiveSessionConnection` types are exported. The hub is not an `/api/v1` route, so it
  adds no route method (docs/11, docs/23).
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
- The package is now **publishable** to the public npm registry instead of being a
  workspace-only `private` package (CORE-PUB-001). `private` is removed and the
  manifest declares the published surface — `publishConfig` (public access +
  registry), `repository`, `sideEffects: false`, a conditional `exports` map and a
  `module` entry alongside `main`/`types` — while `files` still ships only `dist`,
  the `CHANGELOG.md`, the AGPL `LICENSE` and the `THIRD-PARTY-NOTICES.md`. The
  `workspace:*` link to `@livecore/contracts` is kept for local development and
  rewritten to the resolved shared version at publish time, so the tarball carries
  no `workspace:` protocol. The typed surface a consumer imports is unchanged. See
  `docs/23_PACKAGE_VERSIONING.md` ("Publishing").
- The manifest now declares `engines` (`node >= 22`) and `repository.directory`
  (`packages/sdk-ts`), completing the publish-shape, and the release publish runs
  with npm build provenance (`--provenance` under a job-scoped `id-token: write`), so
  each published version carries a verified provenance attestation linking the tarball
  to this pipeline (CORE-PUB-004). Manifest metadata and publish process only — the
  typed surface a consumer imports is unchanged. See
  `docs/23_PACKAGE_VERSIONING.md` ("npm build provenance").

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
