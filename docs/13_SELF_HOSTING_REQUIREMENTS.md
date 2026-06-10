# Self-hosting Requirements

The Core must be self-hostable from the beginning.

## Required runtime configuration

- database connection string
- OIDC issuer/audience/client configuration
- object storage endpoint and credentials
- realtime backplane optional
- CORS allowed origins
- public URL
- logging level
- encryption/signing keys

## Local development

Local dev should run with Docker Compose through `livecore-deploy`.

Services:

```text
api
worker
postgres
keycloak
valkey
rustfs
web or test client where applicable
```

## Production options

- single VPS with Docker Compose
- Railway multi-service deployment
- Kubernetes with Helm

## Operational requirements

- health endpoints
- readiness endpoints
- database migrations
- backup/restore
- structured logs
- metrics
- graceful shutdown
- secret management
