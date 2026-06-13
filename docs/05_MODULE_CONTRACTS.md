# Module Contracts

## IdentityAccess

Owns:

- authenticated principal mapping
- OIDC claims normalization
- user profile reference
- service account support

May not:

- implement custom password auth
- own workspace authorization decisions

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
