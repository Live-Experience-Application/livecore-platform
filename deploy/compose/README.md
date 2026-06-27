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

| Service        | Role                                                                                                                                                               |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `keycloak`     | OIDC provider. Imports the bundled `livecore` realm, so the api validates real bearer tokens — **authenticated traffic**.                                          |
| `rustfs`       | The default example S3-compatible object store (Apache-2.0 RustFS), private buckets only — **asset upload/download**. Any S3-compatible provider works (ADR 0006). |
| `rustfs-setup` | one-shot job (the AWS CLI, an S3-standard client): waits for RustFS, creates the private `livecore-assets` bucket, exits `0`.                                      |
| `valkey`       | Redis/Valkey realtime backplane — **multi-instance realtime** (SignalR scale-out).                                                                                 |

Bring up the merged stack from this directory:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml up -d --build
```

Compose merges the overlay onto the base manifest: the `keycloak`/`rustfs`/`valkey`
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
The bundled admin/credential defaults (`admin`/`admin`, `rustfsadmin`/`rustfsadmin`,
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

**Object storage.** RustFS publishes its S3 API on the host (`http://localhost:9000`)
and a web console (`http://localhost:9001`, login `rustfsadmin`/`rustfsadmin` by
default) for inspecting the private `livecore-assets` bucket — the RustFS equivalent of
any vendor object-store console, for management only; the api/worker talk to the
in-network `http://rustfs:9000` endpoint. RustFS is only the **default example**: set
the `Assets__Storage__*` keys to any S3-compatible provider to bring your own (ADR
0006). Override the published ports with `LIVECORE_RUSTFS_PORT` /
`LIVECORE_RUSTFS_CONSOLE_PORT` and the login with `RUSTFS_ACCESS_KEY` /
`RUSTFS_SECRET_KEY` in `.env`.

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

## Image-only overlay — pull a pinned Core (CORE-OPS-016)

The base stack **builds** the migrate/api/worker images from the in-repo
Dockerfiles, which is the right default for a from-source self-hoster. But a
downstream end-to-end harness (and any deployment) often wants to **pull a pinned,
pull-only Core** instead — building the migrations image from Core source would
cross the no-vendor boundary. All three runtime images are **published to GHCR**,
version-pinned, signed and SBOM-attested on every release (CORE-OPS-009 /
CORE-OPS-015), so the opt-in overlay [`docker-compose.images.yml`](docker-compose.images.yml)
points the three services at those published coordinates and carries **no `build:`
stanza**:

```text
ghcr.io/<owner>/livecore-migrations:<version>
ghcr.io/<owner>/livecore-api:<version>
ghcr.io/<owner>/livecore-worker:<version>
```

Pin the exact release in `LIVECORE_VERSION` (it is **required** — pulling a pinned
Core means naming the version, never a moving tag), then merge the overlay **last**
and skip the build:

```bash
# Pull the pinned, published images, then run them without building:
LIVECORE_VERSION=0.3.0 \
  docker compose -f docker-compose.yml -f docker-compose.images.yml pull
LIVECORE_VERSION=0.3.0 \
  docker compose -f docker-compose.yml -f docker-compose.images.yml up -d --no-build
```

The base file still carries `build:` for the from-source path; the explicit `pull`
and `--no-build` keep this path image-only. Set `LIVECORE_IMAGE_REGISTRY` /
`LIVECORE_IMAGE_OWNER` in `.env` to pull from your own registry or mirror. Combine it
with the production overlay for a pinned, Production-posture Core:

```bash
LIVECORE_VERSION=0.3.0 docker compose \
  -f docker-compose.yml -f docker-compose.images.yml -f docker-compose.prod.yml \
  up -d --no-build
```

The migrate-before-API gate, the probes and the resource ceilings are inherited from
the base manifest unchanged — this overlay only swaps "build from source" for "pull a
pinned, published image".

**Tested.** `scripts/test-compose-deploy.ps1` statically validates that the overlay
references the published api + migrations coordinates and contains no `build:` stanza
(no Docker needed).

## Build the images locally for a downstream vertical (`images:local`, CORE-DXL-001)

The image-only overlay above pulls a **released, pinned** Core from GHCR. The
mirror-image need is a downstream vertical that wants to run an **unreleased** Core —
the Core revision it is developing against — through its **own end-to-end test
harness**, before any release is cut and **without a registry publish**. The base
manifest already builds exactly the images that harness needs, so the root
convenience script makes that one step, with no need to know the compose internals:

```bash
pnpm run images:local
```

It is a thin, **additive** wrapper over the base manifest's existing `build:` stanzas
— `docker compose -f deploy/compose/docker-compose.yml build migrate api worker` — and
changes no existing build, publish or release flow. It builds the three Core runtime
images from source to stable **local** tags:

```text
livecore-api:local
livecore-worker:local
livecore-migrations:local
```

These are the **same tags** the base manifest runs, so after building them a vertical's
harness can bring an unreleased Core up with the image-only overlay's `--no-build`
posture (the images already exist locally), or reference the tags directly from its own
Compose/Kubernetes manifests, or boot a single image to probe it — for example the API
answers `GET /health/ready` once its database schema is current (the migrate gate
above). The script is **idempotent**: a re-run rebuilds the same `:local` tags (Docker
reuses unchanged layers), so a harness can call it before every test run.

The two coordinates a vertical pins are the **stable tag string** (these `:local`
tags) and the **Core revision** its working tree is checked out at — there is no
version number and no registry round-trip in this loop, which is exactly what keeps it
fast for local coupled development. When the Core change is ready to ship, cut a real
release and consume the published, version-pinned `ghcr.io/<owner>/livecore-*:<version>`
images via the image-only overlay instead (the normal release path).

**Tested.** `scripts/test-images-local.ps1` statically validates (no Docker) that the
`images:local` script builds the three Core services from source via the base manifest
and stays additive, that the manifest still tags the three `:local` images, and that
this section documents the contract; the `images-local-smoke` CI job adds the real half
— the script produces the three `:local` images, a re-run is idempotent, and the API
answers `/health/ready` when run from the `:local` image.

## The local-consume contract for a downstream vertical (CORE-DXL-003)

The two convenience scripts each hand a vertical **one half** of an unreleased
Core: `images:local` (above, CORE-DXL-001) builds the **runtime** to local image
tags, and `pack:local` ([root `README.md`](../../README.md), CORE-DXL-002) packs
the **published TypeScript surface** to `dist/` tarballs. A vertical running an
unreleased Core — the Core revision it is developing against, before any release is
cut and **without a registry publish** — through its **own** end-to-end test harness
needs **both**, plus one rule that keeps its version lockstep green. This is that
contract in one place.

**The two coordinates.** To run an unreleased Core locally a vertical pins exactly
two coordinates, each produced by an additive, read-only root script that needs no
registry:

| Coordinate                                                                                                                         | Produced by                            | What it is                                                                                                      |
| ---------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------- | --------------------------------------------------------------------------------------------------------------- |
| the three runtime image tags `livecore-api:local`, `livecore-worker:local`, `livecore-migrations:local`                            | `pnpm run images:local` (CORE-DXL-001) | the API, worker and one-shot migrations runner built from source — the deployable runtime the harness brings up |
| the four `dist/*.tgz` package tarballs (`@livecore/contracts`, `@livecore/sdk-ts`, `@livecore/design-tokens`, `@livecore/ui-core`) | `pnpm run pack:local` (CORE-DXL-002)   | the published TypeScript surface the vertical's app compiles and links against                                  |

Build both from the repository root (each is additive and read-only — neither
pushes, publishes nor commits anything):

```bash
pnpm run images:local   # -> livecore-{api,worker,migrations}:local image tags
pnpm run pack:local     # -> dist/livecore-{contracts,sdk-ts,design-tokens,ui-core}-<version>.tgz
```

The harness then runs the runtime from the `:local` images (the image-only
`--no-build` posture above — the images already exist locally) and installs the four
tarballs into the vertical's app (for example via an env-gated `.pnpmfile.cjs` that
rewrites `@livecore/*` to the `dist/` tarball paths, so the vertical's committed
`package.json`/lockfile stay unchanged).

**Keep the version number unchanged in the inner loop.** This is the rule that keeps
the loop fast and the consumer's pinned-version lockstep green. In the local coupled
loop you change **only code**, never the package version number:

- the four packed tarballs carry the **current** shared package version
  **unchanged** (today `0.5.0`), so a consumer that pins that version keeps its
  cross-package **lockstep guard green** — the tarball it installs still reports the
  version it expects; and
- the `:local` image tags carry **no version at all** — the only thing that moves
  between iterations is the Core working-tree revision the images and tarballs were
  built from.

So the two coordinates a vertical actually pins are a **stable tag string** plus the
**package version it already expects** — there is no version bump and no registry
round-trip in the inner loop, which is exactly what keeps local coupled development
fast.

**A real version bump is the normal release path.** When the Core change is ready to
ship, the version number _does_ move — but through a real release, never the inner
loop: bump the four packages in lockstep and cut a release
([`docs/23_PACKAGE_VERSIONING.md`](../../docs/23_PACKAGE_VERSIONING.md), "How to cut
a release"), which publishes the version-pinned `@livecore/*` packages to npm and the
`ghcr.io/<owner>/livecore-*:<version>` images to GHCR. The vertical then consumes
those **released, version-pinned** coordinates — the published packages by their new
version and the published images via the image-only overlay above — instead of the
`:local` / `dist` pair. Bumping the version inside the inner loop is exactly what the
keep-version rule avoids: it would break a pinned consumer's lockstep for no shipping
benefit.

This section documents the two existing additive scripts as one contract; it makes
**no source or contract change**.

**Tested.** `scripts/test-local-consume-docs.ps1` statically validates (no Docker, no
build) that this section names both the `images:local` and `pack:local` coordinates,
the three `:local` image tags and the four `dist/*.tgz` tarballs, the
keep-the-version-unchanged lockstep rule and the real-version-bump release path, and
that [`docs/23_PACKAGE_VERSIONING.md`](../../docs/23_PACKAGE_VERSIONING.md) points
back here — so a future edit that drops half the contract fails the build. The
forbidden-core-terms boundary scan and the docs gates stay green.

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

## Resource limits and capacity sizing (CORE-DEP-007)

Every service declares a default `deploy.resources.limits` ceiling (cpus + memory)
so an unbounded process cannot starve a single-VPS host. Compose v2 honors these on
`docker compose up`, and each is **overridable via env** — set the variable in `.env`
(see [`.env.example`](.env.example), "Container resource limits"):

| Service    | Default limit (cpus / memory) | Override env vars                                     |
| ---------- | ----------------------------- | ----------------------------------------------------- |
| `api`      | 1.0 / 1024M                   | `LIVECORE_API_CPUS` / `LIVECORE_API_MEMORY`           |
| `worker`   | 0.75 / 768M                   | `LIVECORE_WORKER_CPUS` / `LIVECORE_WORKER_MEMORY`     |
| `postgres` | 1.0 / 1024M                   | `LIVECORE_POSTGRES_CPUS` / `LIVECORE_POSTGRES_MEMORY` |
| `migrate`  | 0.5 / 512M                    | `LIVECORE_MIGRATE_CPUS` / `LIVECORE_MIGRATE_MEMORY`   |

These defaults are the **recommended** baseline; `docs/13_SELF_HOSTING_REQUIREMENTS.md`
("Container resource limits and capacity sizing") gives the **minimum/recommended**
sizing per component, the whole-host baseline, and **when to add API replicas plus the
realtime backplane** (CORE-OPS-007) and sticky sessions (CORE-DEP-002) as load grows.
The migrate runner exits after applying the schema, so the steady-state footprint is
`postgres` + `api` + `worker`. `scripts/test-compose-deploy.ps1` asserts every service
carries a limit.

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

To run the **published** release images (CORE-OPS-009 / CORE-OPS-015) instead of
building from source, merge the image-only overlay (above) — it points migrate/api/worker
at the pinned `ghcr.io/<owner>/livecore-{api,worker,migrations}:<version>` coordinates with
no `build:` stanza: `LIVECORE_VERSION=<version> docker compose -f docker-compose.yml -f
docker-compose.images.yml up -d --no-build`.

For Kubernetes (the third production option in `docs/13`), the repository ships a
**Helm** chart at [`../helm/livecore`](../helm/livecore/README.md) (CORE-DEP-004)
that mirrors this stack's contract: the migrate gate becomes a pre-install/pre-upgrade
`Job`, the probes become `livenessProbe`/`readinessProbe` `httpGet` blocks, and the
env contract becomes a `ConfigMap`/`Secret` projected with `envFrom`.
