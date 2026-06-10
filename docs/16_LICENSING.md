# Licensing Strategy

## Recommended starting point

Core may be licensed AGPL-3.0-or-later if you want a strong open-source/self-hosting model.

## Important warning

AGPL can affect network software and modified server-side deployments. If you want proprietary enterprise offerings, plan a dual-license strategy before public adoption.

This is not legal advice.

## Practical strategy

```text
livecore-platform
  AGPL-3.0-or-later + commercial dual-license option later

arcanos-app
  AGPL if open source; commercial/license review if you want a closed commercial app

scenarioos-enterprise
  private until legal strategy is confirmed

livecore-deploy
  align with Core or use a compatible documentation/deployment license
```

## Dependency review

Every new dependency must be checked for:

- license compatibility
- maintenance status
- security posture
- necessity
