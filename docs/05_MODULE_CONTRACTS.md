# Module Contracts

## IdentityAccess

Owns:

- authenticated principal mapping
- OIDC claims normalization
- user profile reference
- service account support
- the trustworthy verified-email fact (CORE-INV-001)

May not:

- implement custom password auth
- own workspace authorization decisions

The verified-email fact (CORE-INV-001) is the principal's `EmailVerified` flag,
consumed fail-closed from the OIDC `email_verified` claim: the caller email is a
trustworthy verified fact only when the provider asserts `email_verified=true`
for a present, valid email; a missing, `false`, malformed or absent
`email_verified` leaves the email unverified. It is distinct from the existing
optional/informational `Email` metadata (which is spoofable on its own) and is
NEVER an authorization input — it only lets later features safely key on the
email (the invitation self-discovery in CORE-INV-002). The flag is available
server-side on the mapped principal; the `/me` principal read is unchanged.

## Organizations

Owns:

- organizations
- organization membership
- tenant boundaries

Provides:

- organization context
- tenant isolation checks

## Workspaces

Owns:

- workspace metadata
- workspace membership
- workspace-level roles

Provides:

- workspace access checks
- workspace member roster (an Owner/Admin administration read over the existing membership aggregate,
  joining audience-safe profile display metadata read-only; CORE-WSM-001)

## Participants

Owns:

- session/workspace participant records
- participant display identity
- participant connection metadata

May not:

- assume participant means player or trainee

## Sessions

Owns:

- session lifecycle
- session status
- active scene pointer

Provides:

- start/pause/end session commands

## Scenes

Owns:

- scene metadata
- scene ordering
- scene relationships

May not:

- contain vertical-specific scene types in source code

## Content

Owns:

- content blocks
- content block revisions
- content type registry

May not:

- decide visibility alone; must use Visibility module

## Entities

Owns:

- generic entities
- entity types
- entity relationships

May not:

- implement NPC/Quest/Incident behavior directly

## Visibility

Owns:

- visibility rules
- audience calculations
- preview-as-participant
- visible state reconstruction

Provides:

- `CanViewResource`
- `GetVisibleResourcesForParticipant`
- `PreviewVisibilityForHost`

Critical:

- This is the central security module.
- Do not duplicate visibility logic elsewhere.

## Realtime

Owns:

- connection tracking
- hub groups
- event delivery
- reconnect replay

May not:

- send unfiltered events

## Assets

Owns:

- asset metadata
- storage adapter
- upload/download authorization
- signed URL creation

## Audit

Owns:

- append-only audit log
- security event records

## Templates

Owns:

- generic template loader
- template validation
- template versioning

May not:

- hardcode vertical behavior

## Exports

Owns:

- export jobs
- export manifests
- user data export
- workspace export

## Recaps

Owns:

- session recaps
- recap projection
- background recap generation for eligible (ended) sessions

May not:

- expose the recap body to the audience without a separate reveal

## Enforced dependency graph (CORE-ARCH-001)

The module contracts above describe what each module owns. This section records which OTHER modules each module is
allowed to reference, and that graph is **enforced** by an automated architecture test
(`tests/LiveCore.Api.UnitTests/Architecture`, `ModuleBoundaryArchitectureTests`) that reads the compiled
`LiveCore.Api` assembly with `NetArchTest.Rules` and fails on any governed-to-governed reference not listed here.
See `docs/02_ARCHITECTURE.md` ("Enforced module dependency graph") for how the graph is modelled (governed domain
modules vs the shared kernel) and why some existing edges form cycles.

The allowed edges below are the current, reviewed reality. A reference to the **shared kernel** — `Persistence`,
`Observability`, `Hosting`, `SystemModule`, and the root `LiveCore.Api` helpers (`CoreProblem`, `ProblemCodes`,
`Pagination`, `EntityTag`) — is always allowed and is not listed. `A -> none` means the module references no other
governed module.

```text
Assets         -> Audit, Content, Entities, Entitlements, IdentityAccess, Organizations, Participants, Visibility, Workspaces
Audit          -> IdentityAccess, Organizations
Content        -> Assets, Audit, IdentityAccess, Organizations, Scenes, Visibility, Workspaces
Entities       -> Assets, Audit, IdentityAccess, Organizations, Visibility, Workspaces
Entitlements   -> Audit, IdentityAccess, Organizations, Workspaces
Exports        -> Assets, Content, Entities, IdentityAccess, Organizations, Participants, Scenes, Sessions, Workspaces
IdentityAccess -> Audit, Organizations, Participants, Workspaces
Organizations  -> Audit, IdentityAccess
Participants   -> IdentityAccess, Organizations, Workspaces
Realtime       -> IdentityAccess, Organizations, Participants, Sessions, Visibility, Workspaces
Recaps         -> IdentityAccess, Organizations, Realtime, Sessions, Workspaces
Retention      -> Assets, Audit, Exports, Recaps, Sessions, Workspaces
Scenes         -> Assets, Audit, Content, IdentityAccess, Organizations, Visibility, Workspaces
Sessions       -> Audit, Entitlements, IdentityAccess, Organizations, Participants, Realtime, Workspaces
Store          -> Audit, Entitlements, IdentityAccess
Templates      -> Audit, Entities, IdentityAccess, Organizations
Visibility     -> Audit, IdentityAccess, Organizations, Participants, Realtime, Sessions, Workspaces
Workspaces     -> Audit, Entitlements, IdentityAccess, Organizations
```

To add or widen an edge, get it reviewed, then update BOTH this list and
`ModuleDependencyGraph.AllowedDependencies`; the doc-sync test (`ModuleContractsDocTests`) fails if they drift.
