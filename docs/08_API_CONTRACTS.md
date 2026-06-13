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

See `csv/api_routes.csv` for route list.

Minimum endpoint groups:

```text
GET    /api/v1/me
GET    /api/v1/organizations
POST   /api/v1/organizations
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
