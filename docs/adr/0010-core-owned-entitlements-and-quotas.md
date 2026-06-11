# ADR 0010 - Core-owned Entitlements and Quotas

## Decision

The Core owns generic entitlement and quota enforcement.

## Reason

Usage limits, premium state and purchase verification must be enforced server-side. Mobile and web clients can display paywalls or plan messaging, but they cannot be trusted as the source of premium truth.

## Consequences

- Core adds generic plan/entitlement/quota concepts.
- Core verifies store receipts/tokens via provider modules.
- Core remains product-neutral and must not contain ArcanOS pricing copy.
- Verticals map generic entitlements to product language.
