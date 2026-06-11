# ADR 0011 - Mobile Ads Stay Outside Core

## Decision

Core may calculate ad eligibility but must not integrate ad SDKs or define ad placements.

## Reason

Ad rendering, consent UI, ATT timing and placement rules are mobile product concerns. Core only answers whether ads are required for a subject based on entitlements and policies.

## Consequences

- `arcanos-mobile` owns ad SDK integration.
- `livecore-platform` exposes ad eligibility contracts.
- Ads may never interrupt live reveal or private message moments.
