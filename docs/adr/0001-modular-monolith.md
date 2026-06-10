# ADR 0001: Modular Monolith

## Status

Accepted for initial implementation.

## Decision

Start with a modular monolith because authorization, visibility and event persistence require strong consistency and simple reasoning. Microservices may be extracted later only with clear operational need.

## Consequences

- All implementation must follow this decision until superseded by a later ADR.
- Any LLM-proposed change requires a new ADR and human approval.
