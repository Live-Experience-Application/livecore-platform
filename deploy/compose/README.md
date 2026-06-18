# LiveCore — Docker Compose deployment

A runnable, in-repo deployment stack (CORE-DEP-001) that wires the Core runtime
components — **PostgreSQL + the migrations runner + the API + the worker** — so an
operator can deploy from this repository alone. It is the manifest behind the
"single VPS with Docker Compose" / "local and small self-hosting" options in
`docs/02_ARCHITECTURE.md` and `docs/13_SELF_HOSTING_REQUIREMENTS.md`.

## Quick start

From this directory (`deploy/compose`):

```bash
docker compose up -d --build
```

Compose builds the images from the in-repo Dockerfiles and brings the stack up in
the correct order (see "The migrate-before-API gate" below):

1. `postgres` starts and becomes healthy (`pg_isready`).
2. `migrate` runs the EF Core migrations to completion and exits `0`.
3. `api` and `worker` start **only after** `migrate` has succeeded.

Then probe the documented endpoints on the host:

```bash
curl -fsS http://localhost:8080/health/live    # API liveness   (restart probe)
curl -fsS http://localhost:8080/health/ready    # API readiness  (traffic probe)
curl -fsS http://localhost:9464/health/live     # worker per-loop heartbeat liveness
curl -fsS http://localhost:9464/metrics         # worker Prometheus scrape
```

Tear it down (add `-v` to also discard the database volume):

```bash
docker compose down
```

## The migrate-before-API gate

The API host **never** applies migrations implicitly on startup — that is unsafe
for a multi-instance rollout where replicas would race to migrate. Instead the
schema is applied by the one-shot `migrate` service (the
`apps/api/Migrations.Dockerfile` runner), and `api`/`worker` depend on it with:

```text
depends_on:
  migrate:
    condition: service_completed_successfully
```

so Compose blocks their start until the migrations runner exits `0`. This is the
Compose equivalent of a Kubernetes pre-install `Job` / init container. The runner
is idempotent, so re-running `docker compose up` applies nothing when the schema
is already current.

## Health, readiness and liveness probes

| Service | Endpoint        | Purpose                                                                             |
| ------- | --------------- | ----------------------------------------------------------------------------------- |
| api     | `/health/live`  | Liveness — the process is up. Wire to the orchestrator restart probe.               |
| api     | `/health/ready` | Readiness — route traffic only while it passes (CORE-OPS-005).                      |
| worker  | `/health/live`  | Per-loop heartbeat liveness — healthy only when every job loop beats (CORE-DR-003). |
| worker  | `/metrics`      | Prometheus scrape of the `LiveCore` job-failure metrics.                            |

The API/worker runtime images deliberately ship **no HTTP client** and define no
in-container `HEALTHCHECK`; probing is done over HTTP from outside the container
(a reverse proxy, a kubelet, or the published host ports above), exactly as the
image design intends.

## License and attribution in the images

The images Compose builds from the in-repo Dockerfiles are legally complete
(CORE-LIC-003): each declares its license with the OCI
`org.opencontainers.image.licenses="AGPL-3.0-or-later"` label (inspect it with
`docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.licenses" }}' <image>`)
and carries the AGPL `LICENSE` and the generated third-party `THIRD-PARTY-NOTICES.md`
attribution inventory under `/licenses`. The published release images
(`ghcr.io/<owner>/livecore-*`) carry the same labels and files. See
`docs/16_LICENSING.md`.

## Configuration

Every setting is an environment variable from the documented contract
(CORE-OPS-008); none is baked into an image. Compose reads optional overrides from
a `.env` file in this directory — copy `.env.example` to `.env`
and fill in what you need. Unset values fall back to safe local defaults, so the
stack runs with no `.env` at all.

## Hardening for production

The bundled stack defaults to `ASPNETCORE_ENVIRONMENT=Development` so it comes up
green with no identity provider or object storage. For a real deployment, in
`.env`:

- set `ASPNETCORE_ENVIRONMENT=Production`;
- set the OIDC `Authentication__Oidc__Authority` and `Authentication__Oidc__Audience`
  (a configured Authority with a blank Audience refuses to start, CORE-OPS-004; the
  readiness gate reports not-ready until both are set, CORE-OPS-005);
- set `ConnectionStrings__Database` (ideally to a managed/external database),
  `AllowedHosts`, `Cors__AllowedOrigins__0`, the `ForwardedHeaders__KnownNetworks__0`
  of your proxy, and the `Assets__Storage__*` object-storage credentials for media;
- terminate TLS at a reverse proxy in front of the API (Core does not terminate
  TLS itself, CORE-OPS-003);
- keep secrets in your platform's secret store, not in a committed file.

To run the **published** release images (CORE-OPS-009) instead of building from
source, replace each service's `build:`/`image:` with the versioned reference, e.g.
`image: ghcr.io/<owner>/livecore-api:<version>`.

For Kubernetes (the third production option in `docs/13`), the repository ships a
**Helm** chart at [`../helm/livecore`](../helm/livecore/README.md) (CORE-DEP-004)
that mirrors this stack's contract: the migrate gate becomes a pre-install/pre-upgrade
`Job`, the probes become `livenessProbe`/`readinessProbe` `httpGet` blocks, and the
env contract becomes a `ConfigMap`/`Secret` projected with `envFrom`.
