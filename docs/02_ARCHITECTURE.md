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
