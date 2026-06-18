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

## Full local stack overlay (CORE-DEP-006)

The bundled stack above is deliberately **minimal** — postgres + migrate + api +
worker, with the OIDC, object-storage and realtime-backplane seams **unset** so it
comes up green with no external services. That is the right default for small
self-hosting, but it means you cannot run an **authenticated, asset-serving,
scale-out** Core locally out of the box.

The optional overlay [`docker-compose.full.yml`](docker-compose.full.yml) closes
that gap. It is a **separate, opt-in** file (so the minimal stack stays minimal)
that adds the three supporting services and **pre-wires** the api/worker to them:

| Service       | Role                                                                                                                      |
| ------------- | ------------------------------------------------------------------------------------------------------------------------- |
| `keycloak`    | OIDC provider. Imports the bundled `livecore` realm, so the api validates real bearer tokens — **authenticated traffic**. |
| `minio`       | S3-compatible object storage (private buckets only) — **asset upload/download**.                                          |
| `minio-setup` | one-shot job: waits for MinIO, creates the private `livecore-assets` bucket, exits `0`.                                   |
| `valkey`      | Redis/Valkey realtime backplane — **multi-instance realtime** (SignalR scale-out).                                        |

Bring up the merged stack from this directory:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml up -d --build
```

Compose merges the overlay onto the base manifest: the `keycloak`/`minio`/`valkey`
services are added, the api/worker get the `Authentication__Oidc__*`,
`Assets__Storage__*` and `Realtime__Backplane__*` env (the **same documented config
contract keys** the base stack leaves unset), and the base **migrate-before-API
gate is preserved** (the api/worker still wait for the migrations runner, and now
also for the supporting services). The same probe ports are published, so the
health/readiness/liveness checks above work unchanged — and `/health/ready` is now
green only once the OIDC, storage and backplane dependencies are all reachable (the
deep readiness probes, CORE-OBS-009), so a passing readiness proves the stack is
wired together end-to-end.

Every supporting-service knob has a safe **local default** (documented in
[`.env.example`](.env.example) under "Full local stack"); override them in `.env`.
The bundled admin/credential defaults (`admin`/`admin`, `minioadmin`/`minioadmin`,
the realm's `demo` user) are well-known **local** values for a throwaway machine —
the same posture as the base stack's `livecore`/`livecore` database login — **not
production secrets**. Harden them (and the realm) before exposing this anywhere.

**Obtaining a token.** The OIDC issuer is the in-network URL
`http://keycloak:8080/realms/livecore`, so a vertical app that obtains tokens should
run **inside this Compose network** and use that authority (the API's
`minimal-consumer` example, CORE-PUB-003, is the reference integration). Keycloak's
admin console is published on the host (`http://localhost:8081`, admin `admin`/`admin`)
for management only. The realm ships a public `livecore-app` client with direct
access grants and an audience mapper, so a local developer can mint a token for the
`demo` user that the api accepts. The hardcoded `organization` claim is a starter —
point it at the slug of an Organization you create through the api.

**Scaling realtime.** With the Valkey backplane wired, multiple api instances fan
realtime out to every connection. Scale with
`docker compose -f docker-compose.yml -f docker-compose.full.yml up -d --scale api=N`
behind a sticky-session load balancer (the api publishes a single host port, so put
a reverse proxy in front when running more than one instance — see the README
"Graceful shutdown and SignalR sticky sessions").

**Tested.** `scripts/test-compose-deploy.ps1` statically validates that the overlay
wires the OIDC/storage/backplane services and that the api/worker reference them
(no Docker needed), and the `compose-full-smoke` CI job brings the merged stack up
and asserts the documented probes answer `200`.

## Production overlay (CORE-OPS-011)

The bundled stack defaults to `ASPNETCORE_ENVIRONMENT=Development` (above) so it
comes up green with no identity provider. That is the right local default, but in a
Development environment **the production readiness gate is inert** (CORE-OPS-005)
and **the OIDC audience guard does not trip** (CORE-OPS-004) — so copying these
defaults onto a real server yields a **green but unauthenticated** API. To stop a
production deployment from accidentally running in the Development posture, the
optional overlay [`docker-compose.prod.yml`](docker-compose.prod.yml) forces the
**Production** posture on the api and worker:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

It sets `ASPNETCORE_ENVIRONMENT` to the **literal** `Production` (not an
interpolated `${...:-Development}` default a stray `.env` could override back), so
once merged the environment **cannot** be Development. A plain `docker compose up`
(base file only) is unchanged and stays in Development, so local development is not
weakened. Combine it with the full local stack overlay when you want the bundled
OIDC/storage/backplane too:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml -f docker-compose.prod.yml up -d --build
```

Once Production is active the [prod-required] values are **enforced** (the intended
fail-closed behavior): `/health/ready` reports not-ready (`503`) until persistence
and OIDC are configured (CORE-OPS-005), and a configured Authority with a blank
Audience makes the api **refuse to start** (CORE-OPS-004). Fill the [prod-required]
OIDC/persistence/edge values in `.env` (see "Hardening for production" below) before
or alongside enabling the overlay.

**Defense in depth.** Even without the overlay, if the api or worker runs in
Development while bound to a **non-loopback** interface (reachable beyond localhost),
both hosts emit a **loud startup warning** naming the exposed bind address — so the
green-but-unauthenticated default cannot ship silently. A Development host bound only
to loopback (the normal dev posture) warns about nothing.

**Tested.** `scripts/test-compose-deploy.ps1` statically validates that the overlay
forces `ASPNETCORE_ENVIRONMENT=Production` on both the api and worker (no Docker
needed).

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
green with no identity provider or object storage. For a real deployment, add the
production overlay (above) so the Production posture cannot be accidentally skipped —
`docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d` — and in
`.env`:

- set `ASPNETCORE_ENVIRONMENT=Production` (the production overlay already forces
  this; set it here too for a `.env`-only deployment that does not merge the overlay);
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
