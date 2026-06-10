# ADR 0002: Separate Vertical Repositories

## Status

Accepted for initial implementation.

## Decision

Core and verticals live in separate repositories to enforce product boundaries. ArcanOS and ScenarioOS consume versioned Core contracts.

## Consequences

- All implementation must follow this decision until superseded by a later ADR.
- Any LLM-proposed change requires a new ADR and human approval.
