# ADR 0008: Server-side Visibility Enforcement

## Status

Accepted for initial implementation.

## Decision

Hidden content is never delivered to unauthorized clients. UI-only hiding is forbidden.

## Consequences

- All implementation must follow this decision until superseded by a later ADR.
- Any LLM-proposed change requires a new ADR and human approval.
