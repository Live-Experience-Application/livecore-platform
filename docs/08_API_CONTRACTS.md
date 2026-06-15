# API Contracts

All APIs are versioned under `/api/v1`.

Use JSON over HTTPS.

Use Problem Details for errors.

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
DELETE /api/v1/organizations/{organizationSlug}/templates/{templateId}
GET    /api/v1/workspaces
POST   /api/v1/workspaces
GET    /api/v1/workspaces/{workspaceId}
POST   /api/v1/workspaces/{workspaceId}/archive
POST   /api/v1/workspaces/{workspaceId}/members
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
GET    /api/v1/workspaces/{workspaceId}/scenes
POST   /api/v1/workspaces/{workspaceId}/scenes
GET    /api/v1/scenes/{sceneId}
POST   /api/v1/scenes/{sceneId}/content-blocks
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

## Optimistic concurrency (CORE-CONC-001, CORE-CONC-006)

The mutable aggregates carry a server-side optimistic-concurrency token (the PostgreSQL
`xmin` row version; see `docs/10_DATABASE_SCHEMA.md`). CORE-CONC-001 covered `Session`,
`VisibilityRule`, `Workspace`, `Participant`, `PurchaseTransaction` and quota usage;
CORE-CONC-006 extended the token to every other in-place-updated aggregate —
`ContentBlock`, `Entity`, `EntityType`, `Scene`, `Asset`, `SubjectEntitlement`,
`ExportJob` and the `UserProfile` reference. When two read-modify-write commands
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
