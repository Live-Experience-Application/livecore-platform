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
POST   /api/v1/sessions/{sessionId}/reveal
POST   /api/v1/sessions/{sessionId}/hide
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
POST   /api/v1/assets/upload-intent
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

## Idempotency

Reveal and hide (un-reveal) execution must be idempotent for client retry.

A repeated reveal or hide request with the same idempotency key must not create duplicate events.
Reveal and hide use separate idempotency scopes, so the same key value may pair a reveal with its hide.

### Idempotency-Key on create and purchase-verification routes (CORE-DX-004)

Unsafe POSTs that create a resource or have money/entitlement effects also honor an `Idempotency-Key`,
so a client or network retry cannot double-create a resource or re-run an external verifier:

- Covered routes: session create, scene create, content-block create, workspace create, asset-link create,
  and the Apple/Google purchase-verification routes.
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
scene-scoped content-block list and the workspace pending-invitations list; the audit-log read was already
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
ahead of the sunset. The two headers are advisory metadata about the route itself — they
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
