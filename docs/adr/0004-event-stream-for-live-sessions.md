# ADR 0004: Event Stream for Live Sessions

## Status

Accepted for initial implementation.

## Decision

Live session state is represented through persisted append-only events so replay, audit, reconnect and recaps are reliable.

## Consequences

- All implementation must follow this decision until superseded by a later ADR.
- Any LLM-proposed change requires a new ADR and human approval.
