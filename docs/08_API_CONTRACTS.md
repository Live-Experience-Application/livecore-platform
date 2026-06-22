# API Contracts

All APIs are versioned under `/api/v1`.

Use JSON over HTTPS.

Use Problem Details for errors. Every Problem Details response additionally
carries a stable, machine-readable `code` (see "Stable error codes" below).

## Common headers

```text
Authorization: Bearer <access-token>
X-Request-Id: <client-generated-id optional>
Idempotency-Key: <required for reveal commands and write actions where retry is possible>
If-Match: <optional weak ETag a consumer echoes back to make a mutation version-conditional (CORE-DX-002)>
```

A single-resource read or mutation of a mutable aggregate also returns an `ETag`
response header (a weak entity-tag carrying the resource's optimistic-concurrency
version; see "ETag and If-Match" below).

A browser/PWA SDK on a different origin can only read a response header the CORS
policy explicitly exposes. The Core API therefore **exposes** the response headers a
browser consumer must read (`Access-Control-Expose-Headers`; see "Rate-limit headers
and browser-readable response headers" below).

## Common error codes

| Status | Meaning |
|---:|---|
| 400 | validation error |
| 401 | missing/invalid authentication |
| 403 | authenticated but not authorized |
| 404 | not found or intentionally hidden |
| 409 | conflict / version mismatch |
| 412 | If-Match precondition failed (stale version; reload and retry) |
| 422 | semantically invalid command |
| 429 | rate limited |
| 500 | server error |

## Stable error codes (CORE-DX-001)

The HTTP status alone is not a contract: several distinct conditions share a
status (a `409` can be a quota refusal, an archived-workspace refusal, an
optimistic-concurrency conflict or a plain state conflict), and the human
`title`/`detail` prose is free to change. So **every** Problem Details response
also carries a stable, machine-readable `code` extension member drawn from the
documented catalog below. A consumer branches on `code`, never on the prose.

The catalog is the server-side source of truth (`apps/api/ProblemCodes.cs`) and
is published to vertical apps as the `ProblemCodes` enum in `@livecore/contracts`
(`packages/contracts/src/problem-details.ts`); a contract test asserts the two
never drift. The values are lower_snake_case and stable once published.

| `code` | Status | Meaning |
|---|---:|---|
| `validation_error` | 400 | request malformed or failed input validation |
| `authentication_required` | 401 | authentication missing or invalid |
| `permission_denied` | 403 | authenticated but not authorized |
| `not_found` | 404 | not found, or intentionally hidden (fail-closed) |
| `conflict` | 409 | generic state conflict (command not legal from current state) |
| `duplicate_resource` | 409 | a uniqueness constraint would be violated (key/slug exists) |
| `quota_exceeded` | 409 | a server-enforced quota would be exceeded |
| `workspace_archived` | 409 | the target workspace is archived and read-only |
| `concurrency_conflict` | 409 | optimistic-concurrency check failed — reload and retry |
| `precondition_failed` | 412 | an `If-Match` precondition failed (stale `ETag`) — reload and retry |
| `unprocessable_entity` | 422 | well-formed but semantically invalid command |
| `payload_too_large` | 413 | request body exceeds the accepted size cap |
| `rate_limited` | 429 | a rate limit was exceeded |
| `internal_error` | 500 | unexpected server error (no internal detail leaked) |
| `service_unavailable` | 503 | a required dependency (e.g. persistence) is not configured |

The three structurally-different `409`s — `quota_exceeded`, `workspace_archived`
and `concurrency_conflict` — are deliberately distinct codes so a consumer can
react to each correctly. A `code` names only the generic class of problem; it
never encodes a resource id, tenant, principal or internal state, so it leaks
nothing (threat T7 in `docs/07_SECURITY_THREAT_MODEL.md`). The global Problem
Details exception handler (CORE-RES-001) reuses the same catalog for
`internal_error`.

## Core endpoints

The complete, authoritative route list is `csv/api_routes.csv` (one row per
mounted `/api/v1` route). `csv/mobile_store_api_routes.csv` additionally
documents the store/entitlement endpoints in their mobile-facing path shape.
See `docs/24_SPEC_CONSISTENCY.md` for the source-of-truth map.

The block below is a representative minimum, not the full list (every entry in
it is a row in `csv/api_routes.csv`):

```text
GET    /api/v1/me
GET    /api/v1/me/invitations
GET    /api/v1/push/vapid-public-key
POST   /api/v1/me/push-subscriptions
DELETE /api/v1/me/push-subscriptions/{subscriptionId}
GET    /api/v1/organizations
POST   /api/v1/organizations
DELETE /api/v1/organizations/{organizationSlug}
GET    /api/v1/audit-logs
DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data
GET    /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export
GET    /api/v1/organizations/{organizationSlug}/templates
POST   /api/v1/organizations/{organizationSlug}/templates
GET    /api/v1/organizations/{organizationSlug}/templates/{templateId}
DELETE /api/v1/organizations/{organizationSlug}/templates/{templateId}
GET    /api/v1/workspaces
POST   /api/v1/workspaces
GET    /api/v1/workspaces/{workspaceId}
POST   /api/v1/workspaces/{workspaceId}/archive
POST   /api/v1/workspaces/{workspaceId}/members
GET    /api/v1/workspaces/{workspaceId}/members
PATCH  /api/v1/workspaces/{workspaceId}/members/{memberId}
GET    /api/v1/workspaces/{workspaceId}/invitations
POST   /api/v1/workspaces/{workspaceId}/invitations/accept
DELETE /api/v1/workspaces/{workspaceId}/invitations/{invitationId}
GET    /api/v1/workspaces/{workspaceId}/sessions
POST   /api/v1/workspaces/{workspaceId}/sessions
GET    /api/v1/sessions/{sessionId}
POST   /api/v1/sessions/{sessionId}/start
POST   /api/v1/sessions/{sessionId}/end
POST   /api/v1/sessions/{sessionId}/cancel
POST   /api/v1/sessions/{sessionId}/participants/{participantId}/join
POST   /api/v1/sessions/{sessionId}/participants/{participantId}/leave
GET    /api/v1/sessions/{sessionId}/events
GET    /api/v1/sessions/{sessionId}/roster
GET    /api/v1/sessions/{sessionId}/me
POST   /api/v1/sessions/{sessionId}/reveal
POST   /api/v1/sessions/{sessionId}/hide
POST   /api/v1/sessions/{sessionId}/visibility-rules
GET    /api/v1/sessions/{sessionId}/visibility-rules
GET    /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}
POST   /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/lock
POST   /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/unlock
GET    /api/v1/sessions/{sessionId}/recap
GET    /api/v1/exports/{exportId}
GET    /api/v1/workspaces/{workspaceId}/scenes
POST   /api/v1/workspaces/{workspaceId}/scenes
POST   /api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder
GET    /api/v1/scenes/{sceneId}
GET    /api/v1/scenes/{sceneId}/content-blocks
POST   /api/v1/scenes/{sceneId}/content-blocks
GET    /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId}
GET    /api/v1/workspaces/{workspaceId}/entities
POST   /api/v1/workspaces/{workspaceId}/entities
GET    /api/v1/workspaces/{workspaceId}/entities/{entityId}
GET    /api/v1/workspaces/{workspaceId}/entity-types
POST   /api/v1/workspaces/{workspaceId}/entity-types
GET    /api/v1/workspaces/{workspaceId}/entity-types/{entityTypeId}
GET    /api/v1/participants/{participantId}/visible-feed
GET    /api/v1/workspaces/{workspaceId}/assets
POST   /api/v1/assets/upload-intent
POST   /api/v1/assets/{assetId}/confirm-upload
GET    /api/v1/assets/{assetId}/download-url
POST   /api/v1/assets/{assetId}/links
DELETE /api/v1/assets/{assetId}/links/{linkId}
DELETE /api/v1/assets/{assetId}
```

## OpenAPI document (CORE-OAS-001)

The API produces an **OpenAPI 3 document** describing every registered `/api/v1`
route, its request/response schema and the Problem Details error shape above. The
document is **generated from the running minimal-API route table** (not
hand-maintained), so it cannot diverge from the routes the host actually mounts.

| Aspect | Detail |
|---|---|
| Format | OpenAPI 3.0 (`Microsoft.AspNetCore.OpenApi`) |
| Scope | the `/api/v1` surface only (infrastructure routes like `/health`, `/metrics`, `/source` and the SignalR hub are excluded) |
| Error shape | the RFC 7807 Problem Details body, including the stable `code` extension, is a named `ProblemDetails` schema component |
| Explorer endpoint | `GET /openapi/v1.json`, served **only outside Production** (no schema-discovery surface in production) |
| Build artifact | committed at `openapi/livecore-v1.json` |
| Drift gate | `scripts/spec-consistency.ps1` check 12 fails when the committed document does not describe exactly the registered routes; the `dotnet` suite asserts it is valid OpenAPI 3 (`OpenApiDocumentTests`) |
| Regenerate | run the smoke suite with `LIVECORE_OPENAPI_UPDATE=1` after an intentional route/schema change |

The document carries only route shapes, generic schema names and the Problem Details
shape — never a secret, tenant identifier or content; the request-DTO XML doc prose is
stripped, so no internal commentary reaches the published contract (threat T7). The
typed `@livecore/contracts` types are hand-written today and will be **generated from
this document** with a drift gate in CORE-OAS-002.

## DTO design rules

- Host DTOs and Participant DTOs are different.
- Participant DTOs must not contain hidden content fields.
- Never include internal authorization rationale in participant responses.
- Include resource version where concurrent updates matter.
- Include server timestamps.

### Participant visible-feed item: the audience-safe projected shape (CORE-APROJ-002)

Each item of `GET /api/v1/participants/{participantId}/visible-feed` carries the audience-safe projection of
one resource the participant may currently see, so a consumer can render the revealed item from the feed
alone, with **no host read**:

| Field | Meaning |
|---|---|
| `resourceType` | the resource kind (`Scene`/`ContentBlock`/`Entity`), the stable enum name |
| `resourceId` | the surrogate id of the visible resource |
| `title` | the resource's **audience-safe label** — a scene's title, an entity's name, a content block's generic kind — or `null` when the resource no longer resolves |
| `body` | the resource's **audience-safe short body**, or `null` when the kind's audience projection exposes none (the case for every current resource kind) |
| `revealedAt` | when the resource became visible to the participant (the reveal time) |
| `revealScope` | the marker distinguishing an `AudienceWide` reveal from a `SelectedParticipant` (private) reveal |
| `locked` | whether the resource is **sealed (locked)** in the way this participant sees it (CORE-VSEAL-001) — a server-asserted authoring lock marking it permanently-restricted, so a consumer can render a locked presentation state. Audience-safe (a boolean fact about an already-visible resource, never host content). `false` for a normally-revealed resource |
| `scheduledRevealAt` | the **scheduled-reveal time** (UTC) of the granting rule in the way this participant sees the resource (CORE-VSEAL-002), or `null` when it has none — so a consumer can render a scheduled presentation state. Audience-safe (a timestamp fact about an already-visible resource, never host content) |
| `attachments` | the audience-safe list of assets attached to the resource (CORE-ALC-002); each entry is an `assetId`, an audience-safe `name` and a `contentType`. Empty (never absent) when the resource has no attachments |

The `title`/`body` are produced **only** through the resource kind's existing role-based **audience**
projection (the same `Participant{Scene,ContentBlock,Entity}Response` shapes the list/read routes use),
**never** the raw host title/body — so the feed item never leaks host-only content (threats T2/T7), most
importantly the content block's host body is never disclosed here. Visibility is **not recomputed** for the
projection: an item is built only for a resource the central `VisibilityPolicy` has already decided the
participant may see, so the feed stays already-filtered and fail-closed (a participant only ever sees items it
may see). The resource label/body is resolved through a Visibility-owned port whose adapter lives in the
composition root, because the central security module may not reference the Scenes/Content/Entities modules
(CORE-ARCH-001).

#### Audience-safe attachments per feed item (CORE-ALC-002)

Each feed item additionally carries an audience-safe **`attachments`** list — the assets attached to the
resource through the existing asset-link join (`AssetLink`, asset → content block / entity, CORE-AST-005), so
an audience surface can **enumerate the attachments of content the participant has been shown and then request
the authorized per-asset download**, with no host read (the vertical adopter gap ARC-GAP-009). Each entry is:

| Field | Meaning |
|---|---|
| `assetId` | the surrogate id of the attached asset — the handle a consumer passes to `getDownloadUrl` |
| `name` | the asset's **audience-safe** display label, or `null` — Core stores no host-facing asset filename and the storage coordinates are host-only (threats T4/T7), so this is the forward-compatible audience-safe slot, `null` today (exactly as the item's `body` slot is) |
| `contentType` | the asset's MIME content type — the same non-sensitive metadata the signed download-url response already discloses to an authorized audience caller |

The list **inherits the resource's audience visibility**: an item is built only for a resource the central
`VisibilityPolicy` has already decided the participant may see, so a **hidden resource's attachments are never
enumerated** (threat T2) — visibility is never recomputed for attachments. Only an **`Available`** (confirmed,
downloadable) asset is listed; a still-`Pending` asset is not advertised. Each entry is audience-safe (an
`assetId` plus an audience-safe `name` and `contentType`), **never** a host-only field (the storage
provider/bucket/object key and the checksum). Listing an attachment **never** grants access to its bytes: the
download of each listed asset still goes through the **server-side `getDownloadUrl` authorization check**
(defence in depth), which re-evaluates the linked resource's session-scoped visibility before minting a signed
URL (CORE-SVIS-003/004). The attachments are resolved through a Visibility-owned port whose adapter lives in
the composition root, because the central security module may not reference the Assets module (CORE-ARCH-001).

### Participant self-identification: own session participant context + the roster `isSelf` marker (CORE-PSELF-001)

An audience surface needs to call the participant-keyed reads (`getParticipantVisibleFeed`, the roster) **for
itself**, but it does not know its OWN surrogate participant id — `GET /api/v1/me` returns only the user
profile plus organization memberships, no participations. The session-scoped self-resolution route closes that
gap:

`GET /api/v1/sessions/{sessionId}/me` (module **Realtime**, roles "Participant (self)", tenant via the required
`?organizationSlug=` query) returns the **caller's OWN** participant context for the session:

| Field | Meaning |
|---|---|
| `sessionId` | the session this context is scoped to (echoed for correlation) |
| `participantId` | the caller's OWN surrogate participant id — the value the participant-keyed reads take |
| `displayName` | the caller's own session-facing display identity |
| `present` | whether the caller currently holds a live realtime connection to this session (the same per-instance presence signal the roster uses) |

The caller's participant is resolved **entirely server-side** from the authenticated principal via the existing
principal-to-participant mapping (`IParticipantRepository.FindByUserAsync`), **never** a client-supplied id, so
a caller can only ever resolve **itself**. The response carries only the caller's own identity — never another
participant and **no host-only field** (the participant's user-account link is absent) — and no authorization
rationale (threats T2/T7). It is fail-closed and **hidden-404**: a caller who is not a participant of the
session (a tenant member who never joined), a removed participant, a foreign-tenant caller and an unknown
session are all an indistinguishable `404`, never `403`.

The **audience** roster projection (`ParticipantRosterParticipant`, returned by
`GET /api/v1/sessions/{sessionId}/roster` to the audience roles) additionally carries a server-computed
`isSelf` boolean: `true` for exactly the caller's OWN entry, `false` for every other participant. It is the
audience-safe counterpart of the host view's host-only `userProfileId` link (an audience member has no user link
to recognize itself by) and leaks **no other participant's** user id — it is derived only from comparing each
participant's id to the caller's own server-resolved participant id, and is `false` for every entry when the
caller is not itself a participant of the session.

### Participant entity projection: the audience-safe entity-type discriminator (CORE-APROJ-003)

The audience-safe entity DTO (`ParticipantEntityResponse`, returned to the audience roles
`Participant`/`Observer`, the audit role `Auditor` and any other role on
`GET /api/v1/workspaces/{workspaceId}/entities` and `/entities/{entityId}`) carries an audience-safe
**entity-type discriminator** alongside the entity id and name:

| Field | Meaning |
|---|---|
| `id` | the surrogate id of the entity — a non-sensitive correlation handle |
| `name` | the entity's **audience-safe label** (its human-readable name) |
| `entityTypeKey` | the entity type's **stable, lower-case natural key** (the `EntityType.TypeKey` slug) — an audience-safe **kind** discriminator |

`entityTypeKey` lets an audience surface **group or filter entities by kind from the list alone, with no host
read**. It is the type's natural **key** (DATA — a canonical slug the same shape as the workspace slug, never
inspected for vocabulary, the template boundary docs/04), **not** the host-only surrogate `entityTypeId`: the
key discriminates kind **without** leaking the internal type id or any attribute content (threats T2/T7). The
full host `EntityResponse` is **unchanged** (it still carries `entityTypeId` and the `attributeValues`
content). The fail-closed projection by role is unchanged — an entity _is_ content, so only the host-content
roles receive the full shape and every other role receives this stripped shape. The key is resolved server-side
from the workspace's own entity types (tenant- and workspace-scoped); a type key that cannot be resolved
degrades to an empty string, never an error. This composes with CORE-APROJ-002, so a revealed visible-feed item
(which names its resource by `resourceType` + `resourceId`) can be tied to its entity's kind through the entity
projection.

### Visibility-rule projection: the audience-safe resource label (CORE-APROJ-004)

The visibility-rule response (`VisibilityRuleResponse`, returned by the create command and by
`GET /api/v1/sessions/{sessionId}/visibility-rules` and `.../visibility-rules/{ruleId}`) carries a
**denormalized, audience-safe `resourceLabel`** for the governed resource alongside `resourceType` + `resourceId`:

| Field | Meaning |
|---|---|
| `resourceType` | the kind of resource the rule governs (`Scene`/`ContentBlock`/`Entity`), the stable enum name |
| `resourceId` | the surrogate id of the governed resource |
| `resourceLabel` | the resource's **audience-safe label** — a scene's title, an entity's name, a content block's generic kind — or `null` when the resource no longer resolves in the rule's own workspace (a dangling rule) |
| `visibility` | the base audience visibility state (`Hidden`/`Visible`) |
| `participantId` | the selected-participant target, or `null` for an audience-wide rule |
| `locked` | whether the rule is **sealed (locked)** — the server-asserted authoring lock (CORE-VSEAL-001) that makes the governed resource permanently-restricted; `true` while a reveal/hide/change targeting the rule is refused with `409`. Orthogonal to `visibility` (not a third state) |
| `scheduledRevealAt` | the optional **scheduled-reveal time** (UTC) of the rule (CORE-VSEAL-002), or `null` when it has none. When set on a Hidden rule the resource stays hidden until then and is automatically revealed by the worker's sweep through the central engine; projected so a consumer can render a scheduled presentation state |
| `createdAt` / `updatedAt` | server timestamps |

`resourceLabel` lets a host render a **per-resource visibility matrix from `listRules` alone** — naming each row
with **no per-resource host read** (raised by vertical adopter ARC-GAP-006). It is the resource's audience-safe
**name**, **not** its full content: it is produced **only** through the resource kind's existing role-based
**audience** projection (the same `Participant{Scene,ContentBlock,Entity}Response` shapes the feed item uses), so it
can never disclose host-only content even to an authoring caller (threats T2/T7) — most importantly a content
block's host body is never surfaced (its label is the generic kind `Text`/`Media`/`Data`). The label is resolved
server-side through a **Visibility-owned same-workspace resource port whose adapter lives in the composition root**,
because the central security module may not reference the Scenes/Content/Entities modules (CORE-ARCH-001); the
lookup is the rule's **own** `(organization, workspace)`, so it never borrows another workspace's resource name, and
a rule whose resource was **deleted** degrades to a `null` label **without error** (the row's identity and state
still render). The list and read stay restricted to the authoring roles and fail closed — a participant can neither
author nor enumerate rules — and a foreign or unknown rule stays an indistinguishable hidden `404`.

### Sealing (locking) a visibility rule (CORE-VSEAL-001)

A visibility rule can be **sealed (locked)** so the governed resource is **permanently-restricted**: while a
rule is locked, a reveal/hide/visibility-change targeting it is refused **fail-closed with `409`**. The lock is
an **orthogonal authoring flag**, **not** a third `VisibilityState`, so the existing binary Hidden/Visible
enforcement and the central recipient resolver are unchanged — an **unlocked** rule behaves exactly as before.
Two commands, restricted to the authoring roles (Owner/Admin/Host/CoHost — the "Seal or unseal visibility rule"
matrix row), set and clear the lock:

```text
POST   /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/lock     ?organizationSlug={slug}
POST   /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/unlock   ?organizationSlug={slug}
```

- The organization slug travels as a **query parameter** (these commands carry **no body**); a missing slug is
  `400`.
- They are **session-scoped** and fail closed: a foreign-tenant, cross-session, unknown rule or a non-member of
  the session's workspace is an indistinguishable hidden `404`; a non-authoring member is `403`.
- They are **idempotent**: re-locking an already-locked rule (or re-unlocking an already-unlocked one) is a
  no-op that still returns `200` with the rule projection (carrying the updated `locked` flag).
- A real lock change is recorded as a `VisibilityRuleLockChanged` audit fact (actor + governed resource +
  before/after lock-state, by id only — never content).
- The `409` a reveal/hide returns when it targets a locked rule carries the generic `conflict` problem code.

### Scheduled reveal (CORE-VSEAL-002)

A visibility rule can carry an optional **`scheduledRevealAt`** timestamp (the "WHEN" half of controlling
visibility, docs/01_PRODUCT_VISION_AND_SCOPE.md), supplied on the create command
(`POST /api/v1/sessions/{sessionId}/visibility-rules`, an optional body field). A **Hidden** rule with a
**future** `scheduledRevealAt` stays Hidden until that time and is then **automatically revealed** by a
background **worker sweep** — which drives the **same central reveal command** as a live host reveal, so the
auto-reveal is gated through the Visibility engine and emits the **normal session events**
(`ContentRevealed`/`VisibilityRuleChanged`, and `SceneActivated` for a Scene) to **exactly the authorized
audience** — it never reveals to an unauthorized participant, and a selected-participant scheduled rule reveals
only to that participant (threats T2/T3/T5). The auto-reveal is recorded as a **system** action (no actor) in
the audit log and the session events, exactly as the recap/retention background jobs record a system action.

- A rule with **no** `scheduledRevealAt` behaves **exactly as before**; a time in the **past** schedules an
  immediate auto-reveal on the next sweep.
- The sweep is **idempotent** (a rule is auto-revealed **at most once** — an auto-revealed rule is no longer
  Hidden, and the auto-reveal uses a deterministic per-rule reveal idempotency key, so overlapping sweeps,
  multiple worker replicas and a manual re-hide can never double-reveal) and **tenant-safe** (each auto-reveal
  is driven scoped to its rule's own tenant/workspace/session).
- `scheduledRevealAt` is projected on the rule and, where audience-safe, on the participant visible-feed item, so
  a consumer can render a scheduled presentation state — a server fact about WHEN the resource is/was scheduled
  to appear, never host content.
- The sweep is an **off-by-default** worker loop, enabled per deployment with
  `Visibility:ScheduledReveal:Enabled=true` (docs/13_SELF_HOSTING_REQUIREMENTS.md).

## Idempotency

Reveal and hide (un-reveal) execution must be idempotent for client retry.

A repeated reveal or hide request with the same idempotency key must not create duplicate events.
Reveal and hide use separate idempotency scopes, so the same key value may pair a reveal with its hide.

### Idempotency-Key on create and purchase-verification routes (CORE-DX-004)

Unsafe POSTs that create a resource or have money/entitlement effects also honor an `Idempotency-Key`,
so a client or network retry cannot double-create a resource or re-run an external verifier:

- Covered routes: session create, scene create, content-block create, workspace create, asset-link create,
  entity create (CORE-DX-009, scope `entity-create:{organizationId}`), and the Apple/Google
  purchase-verification routes.
- The header is OPTIONAL on these routes (unlike reveal/hide, where it is required): omitting it preserves
  the prior behavior, so it is non-breaking. A present-but-malformed key (over the length bound or carrying
  control characters) is a 400, returned only after authorization.
- On the first request the key is recorded against the produced resource's id, bounded by the existing
  `idempotency_keys` store (its `result_id` column). A retry under the SAME key returns the ORIGINAL result
  (`200 OK`) and creates nothing; a create's first response is `201 Created`, its replay `200 OK`. A retried
  purchase verify short-circuits BEFORE the external verifier and BEFORE any audit fact, returning the
  originally recorded transaction.
- Replay is scoped per tenant and per operation for the create routes (`<operation>:{organizationId}`), and
  per buyer subject for the purchase routes (a purchase is named globally and carries no tenant), so one
  tenant's or buyer's key never resolves another's resource. A different key creates a new resource.

The typed SDK (`@livecore/sdk-ts`, CORE-DX-008) exposes this on exactly these create routes: the
`workspaces.create`, `sessions.create`, `scenes.create`, `content.createBlock`, `assets.createLink` and
`entities.create` (CORE-DX-009) methods each accept an optional trailing options argument carrying an
`idempotencyKey`, forwarded as the `Idempotency-Key` header. The option is optional (contrast
`visibility.reveal`/`hide`, where the key is required), so omitting it is unchanged and non-breaking; a retry
under a reused key replays the original resource the server dedupes instead of creating a duplicate.

## Pagination (CORE-DX-003)

A list endpoint must never return an unbounded array: a single read could otherwise materialize a whole table,
a consumability problem and a DoS/amplification surface (threat T9 in `docs/07_SECURITY_THREAT_MODEL.md`). So
**every** list endpoint is bounded and returns a stable `items + hasMore` page envelope, modelled on the
audit-log read (`AuditLogPageResponse`):

```jsonc
{ "offset": 0, "limit": 50, "hasMore": true, "items": [ /* … */ ] }
```

- The optional `limit` query parameter is the page size: **default `50`**, **clamped to a maximum of `200`** (a
  larger request is silently reduced to the maximum, never rejected). A present-but-malformed `limit` (not a
  positive integer) is a `400`.
- The optional `offset` query parameter is the zero-based start of the page (**default `0`**). A
  present-but-malformed `offset` (not a non-negative integer) is a `400`. Request the next page at
  `offset = offset + items.length`.
- `hasMore` tells the client whether at least one further item exists after this page, computed without a second
  `COUNT` (the server over-fetches one row). The paging parameters are validated **after** authorization, so an
  unauthorized caller never receives request-shape feedback (the audit read's rule).
- The page items keep the endpoint's existing per-item shape, including any **role projection** (for example the
  scene/entity/content-block lists still project the host vs participant DTO per item); only the SET is bounded.
  As with any collection response, a list item carries no per-item `ETag`/`version` (conditional requests target
  a single resource).

The paginated list endpoints are the workspace list, the workspace-scoped session/scene/entity lists, the
scene-scoped content-block list, the workspace pending-invitations list and the caller's own
pending-invitation self-discovery read (`GET /api/v1/me/invitations`, CORE-INV-002, matched only on the
caller's verified email and scoped to the caller's claimed tenants); the audit-log read was already
paged. Single-resource reads (for example `GET /api/v1/sessions/{sessionId}`) return the resource directly, not
a page.

## Optimistic concurrency (CORE-CONC-001, CORE-CONC-006, CORE-WS-006)

The mutable aggregates carry a server-side optimistic-concurrency token (the PostgreSQL
`xmin` row version; see `docs/10_DATABASE_SCHEMA.md`). CORE-CONC-001 covered `Session`,
`VisibilityRule`, `Workspace`, `Participant`, `PurchaseTransaction` and quota usage;
CORE-CONC-006 extended the token to every other in-place-updated aggregate —
`ContentBlock`, `Entity`, `EntityType`, `Scene`, `Asset`, `SubjectEntitlement`,
`ExportJob` and the `UserProfile` reference; CORE-WS-006 added `WorkspaceInvitation`,
which becomes in-place-updated when an invitation is redeemed (`Pending -> Accepted`),
so two concurrent redemptions of one single-use token cannot both grant a membership.
When two read-modify-write commands
interleave on the same row, the second write fails the row version check and the API
returns **`409 Conflict`** instead of silently overwriting (losing) the first writer's
update. The caller's correct response is to reload the resource and retry the command
against the fresh state.

The token is enforced **server-side**; the client never has to send it. The `409`
Problem Details carries only the generic "modified by a concurrent request" reason and
no resource, tenant or internal state (threat T7). This is distinct from the
state-machine `409`s (for example starting a non-`Prepared` session): both are
conflicts, but a concurrency `409` means "the resource changed underneath you, retry",
while a state `409` means "the command is not legal from the current state".

### ETag and If-Match (CORE-DX-002)

The server-side `409` above only catches a race **within one request** (two writes that
interleave at `SaveChanges`). It does nothing for a consumer doing **GET-then-PUT across
HTTP**: between the read and the write the consumer holds a stale copy, and without a way
to assert "only write if unchanged" the second writer silently wins (last-write-wins).
CORE-DX-002 closes that gap by surfacing the same `xmin` token over HTTP:

- A single-resource **read or mutation** of a mutable aggregate returns the token as a
  **weak `ETag`** response header (`ETag: W/"<version>"`) and as a `version` field on the
  response body (docs rule "Include resource version where concurrent updates matter").
  A collection (list) response carries no per-item `ETag`; conditional requests target a
  single resource.
- A **mutating** route accepts an optional **`If-Match`** request header. The consumer
  echoes back the `ETag` (or the bare `version`) it last read. The server compares the
  supplied tag against the resource's current token **before** the write:
  - **match** (or `If-Match: *`) → the write proceeds;
  - **stale** → **`412 Precondition Failed`** (`code` `precondition_failed`), the write is
    refused and nothing changes — the consumer reloads and retries;
  - **absent** → the current behavior is preserved (the write is unconditional; the
    in-request `xmin` `409` is still the backstop against a genuine race).

So a stale `If-Match` is the **before-the-write** counterpart of the in-request
concurrency `409`: together they make two clients' read-modify-write resolve to exactly
one winner — the loser gets `412` (it never saw the current version) or `409` (it raced at
commit), never a silent clobber. The `412` body, like the `409`, names only the generic
"the resource has changed" reason and leaks no resource, tenant or internal state
(threat T7). The token is the existing PostgreSQL `xmin` row version (CORE-CONC-006); the
weak validator and `If-Match` comparison ignore the weak/strong indicator and the quoting,
so a consumer may send back either the exact `W/"..."` header or the bare `version` value.

## Rate-limit headers and browser-readable response headers (CORE-DX-005)

A browser `fetch` from a different origin can read only the CORS "simple" response
headers unless the server explicitly **exposes** the rest. So a vertical's browser/PWA
SDK could not read the headers it needs to behave correctly — the weak concurrency
`ETag`, the `Retry-After`/`RateLimit-*` rate-limit signals, the `Location` of a created
resource, or the request-id correlation header — even though the server sends them. The
single CORS policy (applied to the REST API and the `/hubs` SignalR endpoint) therefore
sets `Access-Control-Expose-Headers` to exactly this set:

| Response header | Set on | Meaning |
|---|---|---|
| `ETag` | single-resource read/mutation | weak optimistic-concurrency validator (CORE-DX-002) |
| `Location` | `201 Created` | path of the freshly created resource |
| `Retry-After` | `429` (and other throttled) | seconds to wait before retrying |
| `RateLimit-Limit` | admitted response and `429` | request quota for the current window |
| `RateLimit-Remaining` | admitted response and `429` | permits remaining in the current window (`0` on a `429`) |
| `RateLimit-Reset` | admitted response and `429` | seconds until the window resets |
| `X-Request-Id` | every response (CORE-OBS-005) | per-request correlation/trace id |
| `traceparent` | every traced response (CORE-OBS-005) | the request span's W3C trace context (`00-<trace-id>-<span-id>-<flags>`) |

The correlation headers (CORE-OBS-005) let a consumer correlate a failed call with the
server's logs and traces. `X-Request-Id` carries the per-request correlation id — a
well-formed inbound `X-Request-Id` the caller supplied (the optional client-generated
request header above, honored only when short and made of a log-safe character set), else
the request's trace id — and it is the **same** value every server log line carries as
`request_id`. `traceparent` is the standard W3C trace context of the request span, so a
caller (or a downstream service) can look the request up in a trace backend; an inbound
`traceparent` is honored, so the server continues the caller's trace. Both are
non-sensitive identifiers (threat T7).

The rate limiter (CORE-SEC-001) emits the IETF draft `RateLimit-Limit`,
`RateLimit-Remaining` and `RateLimit-Reset` headers on **both** an admitted response
(on the authenticated per-principal surface — the surface a browser SDK consumes) and a
`429` rejection, where `RateLimit-Remaining` is `0` and `RateLimit-Reset` matches
`Retry-After`. None of these headers carries a tenant, principal or resource identifier —
they are a version validator, a created-resource path, a correlation id and numeric
ceilings/counts — so exposing them leaks nothing (threat T7). A `429` body remains RFC
7807 Problem Details with the `rate_limited` `code`.

The typed SDK surfaces these instead of discarding them: `@livecore/contracts`
`ResponseHeaders` names each header, and a non-success response raises a
`LiveCoreApiError` carrying `retryAfter` (seconds) and `rateLimit` (`{ limit, remaining,
reset }`), so a consumer can honor the server's back-off rather than guess.

## API evolution: additive-only changes, deprecation and sunset (CORE-DX-006)

API versioning is the `/api/v1` path literal alone, so without an explicit rule any
contract change forces a whole-version cutover with no advance signal — a vertical only
finds out a route or field changed when its calls break. Two conventions close that gap.

**Additive-only evolution.** Within a version, the Core API changes **additive-only**: a
non-breaking change ADDS an OPTIONAL field, a new endpoint, or a new enum/event member,
and ships under the same `/api/v1` version. A change that **removes, renames or narrows**
an existing field/route/value, or **widens a required input**, is breaking and requires a
new version — never an in-place edit of `v1`. This is the same MINOR-vs-MAJOR rule the
published TypeScript contracts follow (`docs/23_PACKAGE_VERSIONING.md`): a consumer
compiled against `v1` keeps compiling and behaving as a version evolves additively.

**Deprecation and sunset headers (RFC 8594).** When a route or field is on its way out it
is flagged deprecated, and the response then carries:

| Response header | Value | Meaning |
|---|---|---|
| `Sunset` | IMF-fixdate (RFC 8594 / RFC 7231), e.g. `Wed, 31 Dec 2031 23:59:59 GMT` | the instant the route is expected to stop responding |
| `Deprecation` | the boolean token `true`, or — when known — the IMF-fixdate deprecation took effect | the route is deprecated (in effect since the given date) |

So a consumer gets the retirement date **before** the contract changes and can migrate
ahead of the sunset. The deprecation carries a **minimum window of 180 days (six months)**
between the `Deprecation` date and the `Sunset` instant — the concrete, enforced stability
commitment documented in `docs/23_PACKAGE_VERSIONING.md` ("API and SDK stability policy and
the path to 1.0", CORE-REL-002). The window is tied to the mechanism: the server's
`DeprecationNotice` refuses to construct a deprecation whose deprecation-to-sunset gap is
shorter than that, so a `Sunset`/`Deprecation` pair can never promise less notice than the
policy states. The two headers are advisory metadata about the route itself — they
carry no tenant, principal or resource content, so they leak nothing (threat T7 in
`docs/07_SECURITY_THREAT_MODEL.md`). They are **exposed via CORS**
(`Access-Control-Expose-Headers`, CORE-DX-005) so a cross-origin browser/PWA SDK can read
them, and `@livecore/contracts` `ResponseHeaders` names `Deprecation` and `Sunset` for a
typed consumer.

The signal is **strictly opt-in**: only a route explicitly flagged deprecated emits the
headers; a current route emits neither, so the headers never pollute a live contract. **No
route is deprecated yet** — this story establishes the policy and the mechanism that honors
it (the server flags an endpoint and the pipeline emits the headers) ahead of the first
deprecation.
