## Recommended production stack

Use a modern, boring, production-friendly stack. Prefer open source and self-hostable tools.

### Backend

- .NET 10 LTS
- ASP.NET Core Web API
- SignalR for realtime
- EF Core for relational persistence
- PostgreSQL as primary database
- Valkey or Redis-compatible backplane for realtime scale-out and cache
- Background worker process for async jobs

### Frontend

- Next.js App Router
- React
- TypeScript strict mode
- PWA-first
- pnpm
- shared design tokens
- shared UI primitives

### Auth

- OIDC-first
- Keycloak for local/self-hosted auth
- compatible with other OIDC providers later
- no custom password system in the application

### Storage

- S3-compatible storage abstraction
- RustFS for self-hosted object storage option
- any S3-compatible provider for hosted environments
- private buckets by default
- signed URLs only after permission checks

### Deployment

- Docker for every service
- Docker Compose for local and small self-hosting
- Kubernetes + Helm for larger production
- Railway supported through Dockerfile-based services

### Security standards

- OWASP ASVS as security requirement baseline
- OWASP API Security Top 10 as API review checklist
- server-side object-level authorization everywhere


# Architecture - livecore-platform

## Architecture style

Use a modular monolith for the Core API.

Do not start with microservices. The domain requires strong consistency around authorization, visibility and event persistence. A modular monolith is easier to reason about and easier for LLM-assisted development to keep clean.

## Runtime components

```text
apps/api
  ASP.NET Core API, SignalR hubs, domain modules

apps/worker
  background jobs, exports, cleanup, async processing

packages/contracts
  OpenAPI-derived TypeScript types, event types, DTO types

packages/sdk-ts
  typed TypeScript client for vertical apps

packages/ui-core
  generic React components and design primitives

packages/design-tokens
  generic tokens and theme contracts
```

The `packages/contracts` types are **hand-written today**: the API now emits an
OpenAPI 3 document (CORE-OAS-001; see "OpenAPI document" under
[API versioning](#api-versioning)), and **generating** the TypeScript contracts from
that document with a drift gate — making "OpenAPI-derived" literally true — is the
next story (CORE-OAS-002).

## Backend module boundaries

```text
IdentityAccess
Organizations
Workspaces
Participants
Sessions
Scenes
Content
Entities
Visibility
Realtime
Assets
Audit
Templates
Exports
System
```

Each module owns its own domain rules. Cross-module access uses application services or explicit contracts, not direct database table ownership violations.

## Request flow

```text
HTTP request
  -> authentication middleware
  -> tenant/workspace context resolver
  -> endpoint/controller
  -> application command/query handler
  -> authorization policy
  -> domain service
  -> repository/unit of work
  -> audit/event append where needed
  -> response DTO
```

## Realtime flow

```text
Host command
  -> authorize command
  -> create SessionEvent
  -> persist append-only event
  -> compute authorized recipients
  -> publish to SignalR groups
  -> clients update state
```

## Clean code rules

- domain logic is not placed in controllers
- authorization is not duplicated in UI
- entity visibility is not computed ad hoc in many places
- every public contract is versioned
- no service class owns too many modules
- if a class name contains `Manager`, review whether it is too broad

## API versioning

Use `/api/v1/...` from the beginning.

Breaking changes require a contract version bump and release notes.

### OpenAPI document (CORE-OAS-001)

The API produces an **OpenAPI 3 document** that describes every registered `/api/v1`
route, its request/response schema and the RFC 7807 Problem Details error shape
(`code` extension; CORE-DX-001). The document is **generated from the running
minimal-API route table** (`Microsoft.AspNetCore.OpenApi`, wired in
`apps/api/Hosting/OpenApiConfiguration.cs`), not hand-maintained, so it can never
diverge from the routes the host actually mounts.

- **Served only outside Production** at `GET /openapi/v1.json`, so a production
  deployment exposes no schema-discovery surface; it is a top-level infrastructure
  route (like `/health/*`, `/metrics` and `/source`), excluded from the versioned
  product surface and from the document it serves.
- **Committed as a build artifact** at `openapi/livecore-v1.json`. A CI gate
  (`scripts/spec-consistency.ps1`, check 12) fails when the committed document does
  not describe exactly the registered routes, and the `dotnet` test suite asserts the
  served document is valid OpenAPI 3 (`OpenApiDocumentTests`). To regenerate the
  artifact after an intentional route/schema change, run the smoke suite with
  `LIVECORE_OPENAPI_UPDATE=1` (see `README.md`).
- **No content leak (threat T7).** The document carries only route shapes, generic
  schema names and the Problem Details shape; the document transformer strips the
  request-DTO XML doc prose so no internal commentary reaches the published contract.

The document is the foundation for the typed TypeScript contracts (CORE-OAS-002),
which will be generated from it with their own drift gate. See
`docs/08_API_CONTRACTS.md` for the contract details and `docs/24_SPEC_CONSISTENCY.md`
for the drift gate.

### Evolution, deprecation and sunset (CORE-DX-006)

The version is just the `/api/v1` path literal, so without a rule the only way to
change anything is a whole-version cutover with no advance signal. The policy that
closes that gap:

- **Additive-only between versions.** A non-breaking change is **additive**: a new
  OPTIONAL field, a new endpoint, or a new enum/event member. It ships under the
  same `/api/v1` version. A change that REMOVES, RENAMES or NARROWS an existing
  field/route/value (or widens a required input) is breaking and requires a new
  version — never an in-place edit of `v1`. This is the same rule the published
  TypeScript contracts follow for a MINOR vs MAJOR change
  (`docs/23_PACKAGE_VERSIONING.md`).
- **Advance signal before retirement.** A retiring route or field is flagged
  deprecated and the API then emits the RFC 8594 `Sunset` header (the date it is
  expected to stop responding) together with the `Deprecation` header, so a vertical
  gets the retirement date **before** the contract changes rather than discovering it
  when a call breaks. The header mechanism and the exact format are documented in
  `docs/08_API_CONTRACTS.md`; the headers are exposed to browser consumers via CORS
  (CORE-DX-005). The convention and mechanism exist even though no route is deprecated
  yet.

## Error format

Use RFC 7807-style Problem Details for API errors.

Never leak sensitive content in errors.

### Global exception handler (CORE-RES-001)

Every error a consumer sees is a Problem Details body — but a contract is only as good as its last-resort
case. Before this story the pipeline (`apps/api/Program.cs`) registered no global handler: the only
translating middleware (`Persistence/ConcurrencyConflictMiddleware`) turns a `DbUpdateConcurrencyException`
into a `409` and **rethrows everything else**, so any other unhandled exception fell through to a bare
framework `500` — not a Problem Details body, and a risk of leaking internal detail.

`UnhandledExceptionProblemDetailsMiddleware` closes that gap. It wraps the whole endpoint pipeline and
translates **any** exception that reaches it unhandled into a fail-closed RFC 7807 Problem Details `500`
carrying the documented `internal_error` code (the same CORE-DX-001 catalog every other error uses, via the
shared `CoreProblem` helper). The layering is deliberate:

- it is registered **outside** (before) the concurrency-conflict middleware, so that one still owns its `409`
  for a `DbUpdateConcurrencyException` while every **other** unhandled exception — including the ones that
  middleware rethrows — funnels through the global handler;
- it sits **inside** the request metrics/tracing spans, so the resulting `500` is observed and a genuine
  server fault **is** counted as a `5xx` error (CORE-OBS-001), while the fail-closed `401`/`403`/`404`/`409`
  the authorization and conflict paths return by design are not.

**No content/PII leak (threat T7).** The exception — its type, message and stack — is logged **server-side**
for the operator (with an identifier-only context: method, route TEMPLATE, correlation id — never the
concrete path or any body content), never echoed to the caller. The response body carries only a generic
title/detail and the stable code; it names no exception type, resource, tenant or internal state. If the
response has already started (the status line is on the wire) the exception is rethrown so the failure stays
loud rather than being half-swallowed, and a client-cancellation is rethrown untranslated so it is neither
turned into a misleading `500` nor counted as a server error.
