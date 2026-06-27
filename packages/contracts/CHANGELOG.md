# Changelog

All notable changes to `@livecore/contracts` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Core SDK and UI packages are released together (lockstep), so every
`@livecore/*` package shares this version. See
`docs/23_PACKAGE_VERSIONING.md` for the versioning and changelog process.

## [Unreleased]

## [0.6.0] - 2026-06-28

Released in lockstep with the other `@livecore/*` packages, which always share one
version. There are no changes to the `@livecore/contracts` typed surface in this
release.

## [0.5.0] - 2026-06-26

### Added

- The generated OpenAPI types now describe the new single-member read route
  `GET /api/v1/workspaces/{workspaceId}/members/{memberId}` (CORE-WSM-003), the read-with-ETag
  counterpart of the member roster that lets a vertical obtain a member's optimistic-concurrency
  token before a role change. The hand-written `WorkspaceMemberResponse` contract is unchanged — the
  per-member token rides on the response `ETag` header, not the body — so this is an additive
  surface-only change (a MINOR change).
- The async export-request contracts `CreateExportRequest` and `ExportJobResponse`, plus the
  `ExportJobStatus` enum (`Pending`/`Running`/`Completed`/`Failed`) (CORE-EXP-003), for the new
  `POST /api/v1/workspaces/{workspaceId}/exports` route. It mints a `Pending` workspace export job
  (returning its `id` — the `exportId` that `GET /api/v1/exports/{exportId}` reads), so the worker
  export producer finally has a queue to drain and the read route can be given a real id. The request
  honors an optional `Idempotency-Key` (a retry replays the original job, never a second one). Additive
  types (a MINOR change).
- The entity-relationship contracts `CreateEntityRelationshipRequest` and
  `EntityRelationshipResponse` (CORE-ENT-008), for the new
  `POST /api/v1/workspaces/{workspaceId}/entity-relationships` create and
  `GET /api/v1/workspaces/{workspaceId}/entity-relationships` list routes. They make
  the entity-relationship graph authorable and readable (a directed edge from a
  `sourceEntityId` to a `targetEntityId` carrying a generic `relationshipKind` slug,
  with a server-assigned id and server timestamps), not only deletable. Both endpoints
  must resolve in the same workspace (an unknown/foreign endpoint or a self-loop is a
  `400`); a duplicate of the same directed edge of the same kind is a `409`. Additive
  types (a MINOR change).
- The entity-search query shape `EntitySearchCriteria` (CORE-ENT-009), for the new
  `GET /api/v1/workspaces/{workspaceId}/entities/search` route. It carries the required
  `organizationSlug` plus the optional `name` (a case-insensitive substring), `entityTypeId`
  and `sessionId` filters that drive server-side filtered entity search, returning the
  role-projected entities (an audience caller only ever receives the entities the server
  reveals to them in the named session — a participant search never returns an unrevealed
  entity). An additive type (a MINOR change).
- The asset download request shape `DownloadUrlRequest` (CORE-DX-010), for
  `GET /api/v1/assets/{assetId}/download-url`. It carries the required `organizationSlug`
  plus an OPTIONAL `sessionId` forwarded as the `?sessionId=` query parameter: a reveal is
  session-scoped, so naming the session takes the session-scoped audience authorization
  path (CORE-SVIS-003/004) and lets an audience (Participant/Observer) caller obtain a
  download URL for an asset revealed to them, which was otherwise a permanent `400`.
  Omitting it preserves the prior host-path behaviour. An additive type (a MINOR change);
  no wire-contract change (the route already honoured the parameter).

## [0.4.0] - 2026-06-23

### Added

- The host workspace member-roster entry `WorkspaceMemberRosterEntryResponse`
  (CORE-WSM-001), the per-item shape of the administration member-roster read
  `GET /api/v1/workspaces/{workspaceId}/members`: the membership `id` (the id
  `removeMember`/`updateMemberRole` address), `organizationId`, `workspaceId`,
  `userProfileId`, the generic `role`, the audience-safe `displayName` (mirrored
  read-only from the profile, `null` when none — never the subject's email) and the
  server timestamps. It is the administration sibling of `WorkspaceMemberResponse`
  (the invitation-redemption projection returned only to the accepting caller) and is
  data-minimized: it carries no invited/login email, no token and no authorization
  rationale (threats T6/T7). An additive type (a MINOR change).
- The member role-change request `UpdateWorkspaceMemberRoleRequest` (CORE-WSM-002) for
  `PATCH /api/v1/workspaces/{workspaceId}/members/{memberId}`: the `organizationSlug`
  and the new generic `role` to assign. The new role must be a defined `MembershipRole`
  (never a vertical term); the last remaining Owner cannot be demoted (a `409`); and the
  change honors `If-Match` optimistic concurrency (a stale ETag is `412`, CORE-DX-002),
  the resource's new version riding on the response `ETag` header rather than the
  `WorkspaceMemberResponse` body. An additive type (a MINOR change).
- The host-facing asset projection `AssetResponse` (CORE-ALC-003): the full,
  product-neutral asset metadata an authoring role enumerates a workspace's uploaded
  assets with — `assetId`, lifecycle `status`, `contentType`, `sizeBytes` and `checksum`
  (both `null` while the asset is still `Pending`, set once `Available`) and the server
  timestamps. It is the per-item shape of the host workspace-asset enumeration read
  `GET /api/v1/workspaces/{workspaceId}/assets` (CORE-ALC-003) and the host per-resource
  attachments read `GET /api/v1/assets/by-target/{targetType}/{targetId}` (CORE-ALC-004).
  It is the host projection — distinct from the audience-safe feed attachments
  (`ParticipantVisibleFeedAttachment`, CORE-ALC-002) — and carries no storage coordinate,
  so listing is never access to the bytes (threat T4). An additive type (a MINOR change).
- The audience-safe current-scene projection on the participant visible feed
  (CORE-APROJ-005): `ParticipantVisibleFeedResponse.currentScene` carries the audience-safe
  `ParticipantSceneResponse` (id/title/order) of the participant's most-recently-revealed
  visible scene — the same visible set `items` is built from, by the reveal time — or
  `null` when no scene is currently visible, so a consumer can render where-we-are-now from
  the feed alone with no host read. It is produced only through the audience-safe scene
  projection, never the raw host scene, so no host-only scene field leaks (threats T2/T7).
  An additive optional field (a MINOR change).

## [0.3.0] - 2026-06-21

### Added

- An audience-safe entity-type discriminator on the audience entity projection
  (CORE-APROJ-003): `ParticipantEntityResponse.entityTypeKey` carries the entity
  type's stable, lower-case natural key (the `EntityType.TypeKey` slug) so an
  audience surface can group or filter entities by kind from the list alone. It is
  data, not host content; the host-only surrogate `entityTypeId` stays omitted. An
  additive optional field (a MINOR change).
- An audience-safe resource label and the authoring lock/schedule on the visibility
  rule projection: `VisibilityRuleResponse` gains `resourceLabel` (a denormalized,
  audience-safe name for the governed resource, or `null` for a dangling rule —
  CORE-APROJ-004), `locked` (the server-asserted seal flag that makes a rule's
  resource permanently-restricted, orthogonal to the Hidden/Visible state —
  CORE-VSEAL-001) and `scheduledRevealAt` (the optional worker-driven auto-reveal
  time — CORE-VSEAL-002). `CreateVisibilityRuleRequest` gains an optional
  `scheduledRevealAt`. All additive (a MINOR change).
- The reveal-scope vocabulary `FeedRevealScopes` / `FeedRevealScope`
  (`AudienceWide`/`SelectedParticipant`, CORE-APROJ-002): the marker distinguishing an
  audience-wide reveal from a private, selected-participant reveal on a participant's
  own feed. A new enum tuple member set (a MINOR change).
- The caller's own session participant context `SessionParticipantContext`
  (CORE-PSELF-001), returned by `GET /api/v1/sessions/{sessionId}/me`: `sessionId`,
  `participantId`, `displayName` and `present`, resolved server-side from the
  authenticated principal so a caller can only ever resolve itself. The audience roster
  participant (`ParticipantRosterParticipant`) gains a server-computed `isSelf` marker.
  Additive (a MINOR change).
- A user-scoped pending-invitation self-discovery type
  `MyPendingWorkspaceInvitationResponse` (CORE-INV-002) for `GET /api/v1/me/invitations`:
  the PII-safe projection of the caller's own pending workspace invitations (the
  `organizationSlug` + `workspaceId` an onboarding flow echoes into `acceptInvitation`),
  carrying no invited email and no token. Additive (a MINOR change).
- The closed-app Web Push registration contracts in a new `push.ts` module
  (CORE-PUSH-001): `RegisterPushSubscriptionRequest`, `PushSubscriptionResponse` and
  `PushVapidPublicKeyResponse` for `GET /api/v1/push/vapid-public-key`,
  `POST /api/v1/me/push-subscriptions` and
  `DELETE /api/v1/me/push-subscriptions/{subscriptionId}`. The `auth` encryption secret
  is write-only and never echoed back (threat T7). Additive (a MINOR change).
- The asset confirm-upload contracts `ConfirmUploadRequest` / `ConfirmUploadResponse`
  (CORE-ALC-001) for `POST /api/v1/assets/{assetId}/confirm-upload`, the `Pending` →
  `Available` transition recording the uploaded size and checksum. Additive (a MINOR
  change).
- The subject's Web Push subscriptions in the personal-data export (CORE-PUSH-001):
  `PersonalDataExportResponse.pushSubscriptions` and
  `PersonalDataExportPushSubscriptionResponse`. The encryption keys are never projected
  into the export. Additive (a MINOR change).

### Changed

- The participant visible-feed item is now a populated, audience-safe projection
  instead of an empty placeholder. `ParticipantVisibleFeedItem` changed from
  `Record<string, never>` to an interface carrying `resourceType` + `resourceId`
  (CORE-APROJ-001 realigned the published type to the server DTO it had drifted from),
  an audience-safe `title` and short `body` (produced only through the resource kind's
  role-based audience projection, never the raw host content), the `revealedAt` reveal
  time, the `revealScope` marker, the `locked` and `scheduledRevealAt` server facts, and
  an `attachments` list of the new `ParticipantVisibleFeedAttachment` (`assetId`,
  audience-safe `name`, `contentType` — CORE-APROJ-002, CORE-VSEAL-001/002, CORE-ALC-002).
  `ParticipantVisibleFeedResponse.items` is no longer always empty. Pre-1.0 the shape
  change ships as a MINOR bump; a consumer that depended on the empty type reads the new
  fields.

## [0.2.0] - 2026-06-19

### Added

- `ResponseHeaders.TraceParent` (`traceparent`, CORE-OBS-005): the W3C trace context
  the server now returns on every traced response so a consumer can look the request up
  in a trace backend, alongside the existing `X-Request-Id` correlation id. Both are
  CORS-exposed so a cross-origin browser SDK can read them. An additive, optional
  response-header constant (a MINOR change). The inbound `traceparent` request header is
  honored, so the server continues the caller's trace.
- `SessionEventReplayResponse.nextSequence` (CORE-PERF-002): the reconnect-replay
  route now returns at most one bounded page of events, and `nextSequence` carries
  the cursor to pass back as `afterSequence` to fetch the next page (or `null` when
  the caller has caught up). It is the page's highest raw per-session sequence, so a
  client always pages forward — even across a full page that contains no event it is
  entitled to — and the cap never silently drops unacknowledged events (docs/11). An
  additive, optional response field (a MINOR change).
- The live realtime hub contract (CORE-RT-007): the stable mirror of the server's
  SignalR live path so a vertical can open the live session stream without
  hard-coding the server's C# constants. `RealtimeHubPaths` (`session` →
  `/hubs/session`) and the `RealtimeHubPath` union, `SESSION_EVENT_CLIENT_METHOD`
  (`SessionEvent`, the single client method the server invokes to deliver an event),
  `REALTIME_ACCESS_TOKEN_QUERY_PARAM` (`access_token`, the query-string token
  parameter for the hub), and the `SessionHubConnectionParams` connection shape
  (`organizationSlug`, `sessionId`, optional own `participantId` — identifiers only,
  never a group name, so a client cannot select a group or another participant's
  feed). The live envelope is the same recipient-safe shape reconnect replay returns,
  exported as `LiveSessionEvent` (= `SessionEventReplayItem`), so a consumer routes a
  live event and a replayed event through one handler. The hub is NOT an `/api/v1`
  route — it is mounted at the origin under `/hubs`, outside the OpenAPI/route surface
  — so these are hand-curated constants pinned by the package tests, not generated
  (docs/11, docs/23). The typed live SDK client that drives them is `@livecore/sdk-ts`.
- The complete, drift-gated session-event contract (CORE-RT-008):
  `KnownSessionEventTypes` now lists all ten emitted Core events
  (`SessionCreated`, `SessionStarted`, `SessionEnded`, `ParticipantJoined`,
  `ParticipantLeft`, `SceneActivated`, `VisibilityRuleChanged`, `ContentRevealed`,
  `ContentHidden`, `RecapGenerated`) rather than just `ContentRevealed`, and each
  gains a typed, identifier-only payload contract — `SessionLifecycleEventPayload`,
  `ParticipantPresenceEventPayload`, `ResourceReferenceEventPayload`,
  `VisibilityRuleChangedEventPayload`, `SceneActivatedEventPayload` and
  `RecapGeneratedEventPayload` — keyed by event type in `SessionEventPayloadMap` and
  exposed as the `ParsedSessionEvent` discriminated union, so a consumer narrows a
  parsed payload by `eventType` instead of blind-parsing the opaque `payload`
  string. The runtime `KnownSessionEventPayloadFields` mirrors each payload's field
  names. The payload field names are PascalCase because the server composes the
  event payload with the default System.Text.Json options (the route DTOs remain
  camelCase). A CI drift gate in the `typescript` job (`check:events`, mirrored by
  the package test) fails if the published event vocabulary or payload fields
  diverge from `csv/event_catalog.csv`, `apps/api/Realtime/SessionEventTypes.cs` and
  `apps/api/Realtime/SessionEventPayloads.cs` — the TypeScript-side mirror of
  spec-consistency check 11 (docs/09, docs/23).
- Contract types for the previously-unmodeled routes so the completed typed SDK can
  route every implemented `/api/v1` route in terms of curated contracts (CORE-SDK-006):
  identity (`CurrentPrincipalResponse`, `CurrentUserResponse`,
  `OrganizationMembershipResponse`), organizations (`CreateOrganizationRequest`,
  `OrganizationResponse`), audit (`AuditLogEntryView`, `AuditLogPageResponse`),
  templates (`CreateTemplateRequest`, `TemplateResponse`), recaps (the role-projected
  `RecapView` / `RecapSummaryView`), and the GDPR data-subject access/portability
  export (`PersonalDataExportResponse` and its subject/membership/participant/invitation
  records). Existing modules gain `CreateSessionRequest` and `ParticipantPresenceResponse`
  (sessions), `ReorderSceneRequest` (scenes), `ParticipantContentBlockResponse` (the
  audience-safe content-block shape), `PendingWorkspaceInvitationResponse` /
  `AcceptWorkspaceInvitationRequest` / `WorkspaceMemberResponse` (workspaces),
  `HideRequest` / `HideResponse` (visibility) and `MeEntitlementsResponse` /
  `EntitlementItem` (entitlements), plus the new enum vocabularies
  `ParticipantStatus`, `ParticipantPresenceOutcome`, `HideOutcome` and
  `EntitlementValueKind` (each as a string-literal union and an `as const` tuple).
  The six request DTOs the package previously left as `OpenApi.components[...]` only
  (`AcceptWorkspaceInvitationRequest`, `CreateOrganizationRequest`,
  `CreateSessionRequest`, `CreateTemplateRequest`, `HideRequest`, `ReorderSceneRequest`)
  now have curated aliases validated against the generated OpenAPI schemas.
- OpenAPI-derived contract types (CORE-OAS-002): the package now generates
  `src/openapi.ts` from the committed OpenAPI 3 document
  (`openapi/livecore-v1.json`, itself generated from the running route table and
  drift-gated against it, CORE-OAS-001) with `openapi-typescript`, and re-exports
  them under the `OpenApi` namespace (for example
  `OpenApi.components["schemas"]["CreateWorkspaceRequest"]`) — so the published
  contracts are literally OpenAPI-derived and cover every request body the server
  declares, including the six not previously modeled with a curated alias. A CI
  drift gate in the `typescript` job (`check:openapi`, mirrored by the package
  build test) regenerates the types and fails on any diff, and the curated
  request DTOs are validated against the generated schemas by the type-tests, so
  the server's contract and the published types cannot silently diverge. The
  curated DTOs remain the primary documented surface (the generator marks every
  required reference-type field `nullable`, an ASP.NET minimal-API quirk).
  `openapi-typescript` is a build-time `devDependency`; the generated output is
  committed, so the package still adds no runtime dependency.
- Deprecation/sunset response header names (CORE-DX-006): `ResponseHeaders` gains
  `Deprecation` and `Sunset`. A deprecated Core API route signals its retirement
  with the RFC 8594 `Sunset` header (the date it stops responding) plus the
  `Deprecation` header, both exposed by the CORS policy so a cross-origin consumer
  can read advance notice before a contract changes; neither carries
  tenant/principal content (threat T7). The Core API evolves additive-only between
  deprecations (a non-breaking change adds an optional field, endpoint or enum/event
  member; it never removes, renames or narrows an existing one), so a current route
  sends neither header.
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
- The package is now **publishable** to the public npm registry instead of being a
  workspace-only `private` package (CORE-PUB-001). `private` is removed and the
  manifest declares the published surface — `publishConfig` (public access +
  registry), `repository`, `sideEffects: false`, a conditional `exports` map and a
  `module` entry alongside `main`/`types` — while `files` still ships only `dist`,
  the `CHANGELOG.md`, the AGPL `LICENSE` and the `THIRD-PARTY-NOTICES.md`. The typed
  surface a consumer imports is unchanged. See `docs/23_PACKAGE_VERSIONING.md`
  ("Publishing").
- The manifest now declares `engines` (`node >= 22`) and `repository.directory`
  (`packages/contracts`), completing the publish-shape, and the release publish runs
  with npm build provenance (`--provenance` under a job-scoped `id-token: write`), so
  each published version carries a verified provenance attestation linking the tarball
  to this pipeline (CORE-PUB-004). Manifest metadata and publish process only — the
  typed surface a consumer imports is unchanged. See
  `docs/23_PACKAGE_VERSIONING.md` ("npm build provenance").

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
