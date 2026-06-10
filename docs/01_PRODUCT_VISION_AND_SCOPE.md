# Product Vision and Scope - Core

## Vision

LiveCore is a self-hostable platform for controlled interactive live sessions.

It enables a host to control what information is visible to which participants, when, and under what context.

## Core product statement

> A product-neutral Live Experience Platform for scene-based sessions, role-aware content delivery, realtime reveals, audit logs and reusable vertical templates.

## Core user roles

Generic roles only:

```text
Owner
Admin
Host
CoHost
Participant
Observer
Auditor
ServiceAccount
```

Verticals may rename roles in their UI, but Core stores and enforces only generic roles.

## In scope

- organizations
- workspaces
- memberships
- sessions
- participants
- scenes
- content blocks
- generic entities
- entity types
- assets
- visibility rules
- reveal events
- session event stream
- audit logs
- exports
- basic recaps
- templates
- OIDC authentication
- server-side authorization
- Docker/self-hosting readiness

## Out of scope

- DnD rules
- Pen-and-Paper terminology
- enterprise training terminology
- marketplace
- billing
- native mobile apps
- AI-generated content in Core v1
- full offline multiplayer sync
- 3D or map engine

## Product quality target

This is production-ready software. Every feature must be designed for maintainability, security, observability and upgradeability from the beginning.
