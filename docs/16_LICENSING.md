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

## AGPL section 13 source offer (CORE-CMP-001)

Because the Core is AGPL-3.0-or-later and network-interactive (the SignalR hub and
the `/api/v1` surface), AGPL-3.0 section 13 obliges a hosted deployment to offer
remote users access to its Corresponding Source. The API host satisfies this with a
small, anonymous endpoint:

| Endpoint      | Purpose                                                                                                       |
| ------------- | ------------------------------------------------------------------------------------------------------------ |
| `GET /source` | Offers the Corresponding Source: the SPDX license, the running build version and where the source is hosted. |

The response is JSON and requires no authentication — the offer is owed to every
remote user the application interacts with:

```json
{
  "license": "AGPL-3.0-or-later",
  "version": "<running build version>",
  "sourceUrl": "https://github.com/Live-Experience-Application/livecore-platform"
}
```

The build version is read from the running assembly, so the offer always identifies
the exact source revision deployed. A deployment that runs **modified** source must
offer **its own** Corresponding Source, so the offered location is
configuration-overridable with `SourceOffer:RepositoryUrl` (env
`SourceOffer__RepositoryUrl`); unset, it falls back to the canonical upstream
repository.

Like `/health/*` and `/metrics`, `/source` is a top-level infrastructure route, not
part of the versioned `/api/v1` product surface (so it is not a row in
`csv/api_routes.csv`). It exposes only the license, a build version and a public
repository URL — never a token, tenant identifier, configuration value or resource
content (threat T7 in `docs/07_SECURITY_THREAT_MODEL.md`).

## Dependency review

Every new dependency must be checked for:

- license compatibility
- maintenance status
- security posture
- necessity
