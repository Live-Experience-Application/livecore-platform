# ADR 0005: OIDC-first Authentication

## Status

Accepted for initial implementation.

## Decision

The application consumes OIDC tokens and does not implement custom password authentication. Keycloak is the default self-hosted provider.

## Consequences

- All implementation must follow this decision until superseded by a later ADR.
- Any LLM-proposed change requires a new ADR and human approval.
