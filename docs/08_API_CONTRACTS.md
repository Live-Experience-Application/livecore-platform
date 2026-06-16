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
```

## Common error codes

| Status | Meaning |
|---:|---|
| 400 | validation error |
| 401 | missing/invalid authentication |
| 403 | authenticated but not authorized |
| 404 | not found or intentionally hidden |
| 409 | conflict / version mismatch |
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
GET    /api/v1/audit-logs
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
