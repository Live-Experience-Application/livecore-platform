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

The repository ships a runnable Docker Compose stack at
[`deploy/compose/docker-compose.yml`](../deploy/compose/docker-compose.yml)
(CORE-DEP-001), so the Core is self-hostable **from this repository alone** — no
separate `livecore-deploy` checkout is required for the minimal stack. See
"In-repo deployment manifest" below. A larger local stack with the full set of
supporting services can still be composed in `livecore-deploy`:

```text
api
worker
postgres
keycloak
valkey
rustfs
web or test client where applicable
```

### In-repo deployment manifest (CORE-DEP-001)

[`deploy/compose/docker-compose.yml`](../deploy/compose/docker-compose.yml) wires
the three Core runtime components an operator must run together —
**PostgreSQL + the migrations runner + the API + the worker** — and is the runnable
form of the "single VPS with Docker Compose" / "local and small self-hosting"
options above. Bring it up from `deploy/compose`:

```bash
docker compose up -d --build
```

It reuses the documented configuration contract (see "Secret management and the
configuration contract" below): every setting is an environment variable, none is
baked into an image, and Compose reads optional overrides from a `.env` file in
that directory (`deploy/compose/.env.example` lists them). The bundled stack
defaults to `ASPNETCORE_ENVIRONMENT=Development` so it comes up green with no
identity provider or object storage configured; for a real deployment merge the
production overlay (`docker-compose.prod.yml`, CORE-OPS-011 — see "Production
overlay and the Development-default footgun" below) so the Production posture cannot
be accidentally skipped, and `deploy/compose/README.md` documents the rest of
hardening it for production (fill the OIDC/storage/CORS values, run published
images, terminate TLS at a proxy).

**The migrate-before-API gate.** The API never migrates on startup (above). The
manifest expresses the run-before ordering with a one-shot `migrate` service (the
`apps/api/Migrations.Dockerfile` runner) that `api` and `worker` depend on:

```yaml
depends_on:
  migrate:
    condition: service_completed_successfully
```

so Compose starts the API and worker **only after** the migrations runner exits
`0` — the Compose equivalent of the Kubernetes pre-install `Job` / init container.
Both also wait for `postgres` to pass its `pg_isready` healthcheck first.

**The documented health/readiness/liveness probes.** The probe endpoints are
published on the host so an orchestrator / reverse proxy probes them over HTTP
(the API/worker images deliberately ship no in-container HTTP client, so probing
is external):

| Service | Endpoint        | Role                                                    |
| ------- | --------------- | ------------------------------------------------------- |
| api     | `/health/live`  | Liveness — restart on failure.                          |
| api     | `/health/ready` | Readiness — route traffic only while passing (CORE-OPS-005). |
| worker  | `/health/live`  | Per-loop heartbeat liveness (CORE-DR-003).              |
| worker  | `/metrics`      | Prometheus scrape.                                       |

**Tested.** `scripts/test-compose-deploy.ps1` statically validates that the
manifest wires the migrate gate, the postgres healthcheck, all four services and
the documented probes (no Docker needed), and the `compose-smoke` CI job
(`.github/workflows/ci.yml`) renders the manifest, brings the stack up and asserts
the migrations runner exits `0`, the API and worker start **only after** it
completes, and every probe endpoint answers `200`.

### Full local stack overlay (CORE-DEP-006)

The minimal stack above deliberately omits the OIDC provider, object storage and
the realtime backplane — `docker-compose.yml` defines only `postgres`/`migrate`/
`api`/`worker`, with those seams **unset** so it comes up green with no external
services. The right default for small self-hosting, but it means nobody can run an
**authenticated, asset-serving, scale-out** Core locally out of the box (the
larger `livecore-deploy` stack listed under "Local development" exists for exactly
that, but it is a separate checkout).

[`deploy/compose/docker-compose.full.yml`](../deploy/compose/docker-compose.full.yml)
is an **optional, opt-in overlay** that closes the gap **from this repository
alone**, while keeping the minimal stack minimal. It adds the three supporting
services from the "Local development" list — `keycloak` (OIDC), `minio`
(S3-compatible object storage, with a one-shot `minio-setup` job that creates the
private `livecore-assets` bucket) and `valkey` (the Redis/Valkey backplane) — and
**pre-wires** the api/worker to them. Bring up the merged stack from
`deploy/compose`:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml up -d --build
```

It is **a separate file, not a change to the minimal stack**: a plain
`docker compose up` is unchanged. Compose merges the overlay onto the base, so:

- **It reuses the documented configuration contract** (CORE-OPS-008): the overlay
  only fills the same `Authentication:Oidc:*` (`Authentication__Oidc__*`),
  `Assets:Storage:*` (`Assets__Storage__*`) and `Realtime:Backplane:*`
  (`Realtime__Backplane__*`) keys the base manifest already exposes unset — pointing
  them at the bundled `keycloak`/`minio`/`valkey` services — so authenticated
  traffic, asset upload/download and multi-instance realtime work with no manual
  setup. Every supporting-service knob has a safe local default, documented in
  `deploy/compose/.env.example` under "Full local stack".
- **It inherits the existing migrate gate.** The merged api/worker still depend on
  the one-shot `migrate` runner with
  `condition: service_completed_successfully`, and now additionally wait for the
  supporting services (the object-storage bucket setup completing, the backplane
  healthy). The gate is preserved, not re-implemented.
- **The probes are unchanged and prove the wiring.** The same `/health/live`,
  `/health/ready` and `/metrics` ports are published. Because `/health/ready` runs
  the deep dependency reachability probes (CORE-OBS-009) for each configured
  dependency, a green readiness on the full stack means the OIDC provider, object
  storage and backplane are all reachable — i.e. the stack is wired together
  end-to-end.

The OIDC issuer is the in-network URL `http://keycloak:8080/realms/livecore`, so a
vertical app that obtains tokens should run inside the Compose network and use that
authority (the `examples/minimal-consumer` reference integration, CORE-PUB-003,
which this overlay underpins). The bundled admin/credential defaults are well-known
**local** values for a throwaway machine — the same posture as the base stack's
`livecore`/`livecore` database login — **not** production secrets; harden them and
the imported realm before exposing the stack. `deploy/compose/README.md` documents
the realm, token issuance and scaling realtime (`--scale api=N` behind a sticky
proxy).

**Tested.** `scripts/test-compose-deploy.ps1` statically validates that the overlay
defines the OIDC/storage/backplane services and that the api/worker reference them
(no Docker needed), and the `compose-full-smoke` CI job (`.github/workflows/ci.yml`)
renders the merged manifest, brings the full stack up and asserts the documented
probe endpoints answer `200`.

### Production overlay and the Development-default footgun (CORE-OPS-011)

The bundled stack defaults to `ASPNETCORE_ENVIRONMENT=Development` so it comes up
green with no identity provider. That is the right LOCAL default, but in a
Development environment **the production readiness gate is inert** (CORE-OPS-005,
below) and **the OIDC audience guard does not trip** (CORE-OPS-004, above) — so
copying the bundled defaults onto a real server yields a **green but
unauthenticated** API. Two changes close that footgun **without weakening the
legitimate local-dev default**:

- **An opt-in production overlay**
  [`deploy/compose/docker-compose.prod.yml`](../deploy/compose/docker-compose.prod.yml)
  forces the **Production** posture on the api and worker. It sets
  `ASPNETCORE_ENVIRONMENT` to the **literal** `Production` (not an interpolated
  `${...:-Development}` default a stray `.env` could override back), so once merged
  the environment **cannot** be Development:

  ```bash
  docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
  ```

  A plain `docker compose up` is unchanged and stays in Development. Once Production
  is active the [prod-required] values are **enforced** (the intended fail-closed
  behavior): `/health/ready` reports not-ready until persistence and OIDC are
  configured (CORE-OPS-005), and a configured `Authority` with a blank `Audience`
  makes the api **refuse to start** (CORE-OPS-004). `scripts/test-compose-deploy.ps1`
  statically validates the overlay forces `Production` on both services.

- **A loud startup warning** as defense in depth: when the api or worker runs in
  Development while bound to a **non-loopback** interface (reachable beyond
  localhost), the host emits a prominent startup warning naming the exposed bind
  address, so the green-but-unauthenticated default cannot ship silently. A
  Development host bound only to loopback (the normal dev posture) warns about
  nothing. The decision is the pure, environment-aware
  `DevelopmentExposureWarning`, mirroring the audience guard (CORE-OPS-004) and the
  readiness gate (CORE-OPS-005); it logs only the bind addresses, never a secret or
  tenant identifier (threat T7).

## Container resource limits and capacity sizing (CORE-DEP-007)

Without a ceiling, any one runtime process can consume all of a host's CPU and
memory and **starve the others** — on a single-VPS Compose deployment a runaway API
or a memory-hungry query can take the whole box down. So the bundled Compose stack
(`deploy/compose/docker-compose.yml`) declares a **default `deploy.resources.limits`
ceiling on every service** (`postgres`, `migrate`, `api`, `worker`). The limits are
**caps, not reservations**, so they never block scheduling on a small host, and
**`docker compose up` honors them** (Compose v2 maps `deploy.resources.limits.cpus`
/ `.memory` to the container `--cpus` / `--memory`). The Kubernetes path sizes the
same ceilings as `resources.requests`/`limits` through the Helm chart
(`deploy/helm/livecore`, CORE-DEP-004), independently of these Compose values.

**Every limit is overridable via env**, so an operator tunes the ceiling to the host
without editing the manifest — set the variable in `deploy/compose/.env`
(`deploy/compose/.env.example` lists them):

| Component                 | Minimum (small/idle) | Recommended (the shipped compose default) | Override env vars                                       |
| ------------------------- | -------------------- | ----------------------------------------- | ------------------------------------------------------ |
| API (`api`)               | 0.5 vCPU / 512 MB    | **1.0 vCPU / 1024 MB**                     | `LIVECORE_API_CPUS` / `LIVECORE_API_MEMORY`            |
| Worker (`worker`)         | 0.25 vCPU / 384 MB   | **0.75 vCPU / 768 MB**                     | `LIVECORE_WORKER_CPUS` / `LIVECORE_WORKER_MEMORY`      |
| PostgreSQL (`postgres`)   | 0.5 vCPU / 512 MB    | **1.0 vCPU / 1024 MB**                     | `LIVECORE_POSTGRES_CPUS` / `LIVECORE_POSTGRES_MEMORY`  |
| Migrations runner (`migrate`) | 0.25 vCPU / 256 MB | **0.5 vCPU / 512 MB**                    | `LIVECORE_MIGRATE_CPUS` / `LIVECORE_MIGRATE_MEMORY`    |

The CPU values are fractional cores (vCPUs); the memory values accept the Compose
suffixes (`512M`, `1024M`, `1g`). Two sizing notes:

- **Whole-host baseline.** The `migrate` runner only runs at deploy time and then
  exits, so the **steady-state** footprint is `postgres` + `api` + `worker`. With the
  recommended defaults that is ≈ **2.75 vCPU / ~2.8 GB** of limits; a single-VPS host
  with **2 vCPU / 4 GB** runs the stack comfortably with headroom for the OS, and a
  **2 vCPU / 2 GB** host should drop each component to the minimum column. The CPU
  limits are caps, not reservations, so their sum may legitimately exceed the host's
  physical cores — the kernel time-shares — while each cap stops any single process
  from monopolizing the box.
- **The DB limit pairs with the connection-pool sizing (CORE-RES-004).** PostgreSQL's
  memory and `max_connections` must fit the container limit; size `Maximum Pool Size`
  in `ConnectionStrings__Database` so all API and worker replicas together stay within
  the database's `max_connections` (see "Database connection tuning"). Container CPU/
  memory limits and the connection-pool cap are complementary controls.

### When to add API replicas and the realtime backplane (CORE-OPS-007)

Vertical scaling (raising the limits above) is the first lever and is enough for most
single-VPS deployments. **Scale the API horizontally — run more than one `api`
instance — when** a single instance saturates its CPU/memory ceiling under sustained
load, when concurrent connection counts outgrow one process, or when you need
high availability (no single point of failure / zero-downtime rolling deploys). The
moment you run **more than one API instance**, two additional controls become
**required**, not optional:

- **A Valkey/Redis realtime backplane (CORE-OPS-007).** SignalR tracks hub group
  membership per-process, so without a shared backplane an event computed on one
  instance reaches only the clients connected to **that** instance and is **silently
  dropped** for everyone connected to the others. Configure
  `Realtime__Backplane__ConnectionString` (see "Realtime scale-out backplane") so
  every instance fans realtime out to all connections. With a single instance the
  in-process backplane is correct and **no** backplane is needed.
- **Sticky sessions / session affinity (CORE-DEP-002).** A SignalR connection's
  negotiate + transport handshake must reach the **same** instance; enable
  cookie/affinity at the reverse proxy or load balancer for the `/hubs` endpoint (see
  "Multi-instance SignalR requires sticky sessions / ARR affinity"). The backplane and
  affinity solve **different** problems and are both required at scale.

Also re-check the **connection-pool sizing** (CORE-RES-004) when adding replicas: more
API/worker instances multiply the pools that share the database's `max_connections`.
The worker is a **singleton by default** (CORE-RES-003 covers multi-replica worker
safety); scale the API for request/realtime load, not the worker, unless that story's
guidance applies.

## Production options

- single VPS with Docker Compose — the in-repo stack at
  [`deploy/compose/docker-compose.yml`](../deploy/compose/docker-compose.yml) (CORE-DEP-001,
  see "In-repo deployment manifest" above)
- Railway multi-service deployment
- Kubernetes with Helm — the in-repo chart at
  [`deploy/helm/livecore`](../deploy/helm/livecore/README.md) (CORE-DEP-004, see
  "In-repo Kubernetes Helm chart" below)

### In-repo Kubernetes Helm chart (CORE-DEP-004)

[`deploy/helm/livecore`](../deploy/helm/livecore/README.md) is a Helm chart that
deploys the same three Core runtime components to Kubernetes — **the API host + the
worker + the one-shot migrations runner** — as the "Kubernetes with Helm for larger
production" option above. It **mirrors the migrate-before-API contract the Compose
stack enforces** (CORE-DEP-001), expressed with the platform's native primitives:

**The migrate-before-API gate.** The migrations runner runs as a
**pre-install/pre-upgrade `Job`** ([`templates/migrate-job.yaml`](../deploy/helm/livecore/templates/migrate-job.yaml)):

```yaml
annotations:
  "helm.sh/hook": pre-install,pre-upgrade
  "helm.sh/hook-weight": "-5"
  "helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded
```

Helm runs a pre-install/pre-upgrade hook **to completion before** it applies the
release's other manifests, and **aborts the release if the hook fails**, so the API
and worker `Deployment`s roll out **only after** the migrations `Job` exits `0` —
the Kubernetes equivalent of the Compose `depends_on:
{ migrate: { condition: service_completed_successfully } }` gate. The runner reads
the **same** `ConnectionStrings__Database` key and is idempotent. The API's own
schema-version readiness check (CORE-OBS-010) is the second line of defence.

**The documented probes.** The API `Deployment` wires the unauthenticated
`/health/live` (liveness — restart) and `/health/ready` (readiness — route traffic,
CORE-OPS-005) probes as `httpGet` blocks; the worker wires `/health/live` (per-loop
heartbeat liveness, CORE-DR-003) and exposes its `/metrics` port. Same endpoints as
the Compose stack and this document's probe table above.

**Externalized configuration, no baked secret.** Every setting is the documented
configuration contract (CORE-OPS-008): non-secret keys (`config:`) render into a
`ConfigMap`, the `[secret]` keys (`secrets:`) into a `Secret` (`type: Opaque`),
projected into all three workloads with `envFrom`. **No secret is committed in the
chart** — every `secrets.*` value defaults to empty and is supplied at install time
or via `secrets.existingSecret` (a `Secret` managed by your secret store). A
`Service` (for the API) and an optional `Ingress` are included; Core does not
terminate TLS itself (CORE-OPS-003), so terminate it at the ingress and forward the
scheme/host/IP.

**Tested.** `scripts/test-helm-chart.ps1` statically validates that the chart wires
the pre-install/pre-upgrade migrate `Job`, the documented probes and the
`ConfigMap`/`Secret` externalization and bakes no secret (no helm/kubeconform
needed), and the `helm-chart` CI job (`.github/workflows/ci.yml`) runs `helm lint`,
renders the chart with `helm template`, **schema-validates** every rendered manifest
with `kubeconform`, and asserts the pre-install migrate `Job`, the probes and that
no secret is hardcoded. See [`deploy/helm/livecore/README.md`](../deploy/helm/livecore/README.md).

## Operational requirements

- health endpoints
- readiness endpoints
- database migrations
- backup/restore
- structured logs
- metrics
- graceful shutdown
- secret management

## Database migrations (CORE-OPS-001)

The schema ships as checked-in EF Core migrations under
`apps/api/Persistence/Migrations`. The API host **never** applies them
implicitly on startup: an implicit startup `Migrate()` is unsafe for a
multi-instance deployment because every replica would race to migrate the same
database. Instead, migrations are applied by a **separate, run-to-completion
deployment step** that must finish **before** the API rolls out, so a fresh
deploy gets its schema applied deterministically before the API serves traffic.

### The migration runner

`apps/api/Migrations.Dockerfile` builds a one-shot **migrations runner image**:
the published API assembly invoked with the `migrate` verb
(`apps/api/Hosting/MigrationCommand.cs`), which applies every pending migration to
the target database and then exits. The `migrate` verb is a distinct invocation,
not a startup hook — it applies the migrations and exits **without ever building
the web host**, so the API host's "never migrate on startup" guarantee is
untouched. It carries no credentials; the connection string is supplied at run
time through the **same** configuration key the API runtime reads,
`ConnectionStrings:Database` (environment variable `ConnectionStrings__Database`).

Build it from the repository root:

```bash
docker build -f apps/api/Migrations.Dockerfile -t livecore-migrations .
```

Apply the migrations (this is the exact command a deploy runs):

```bash
docker run --rm \
  -e ConnectionStrings__Database="Host=<db-host>;Port=5432;Database=<db>;Username=<user>;Password=<password>" \
  livecore-migrations
```

The runner exits `0` on success; re-running it when the database is already up to
date applies nothing and still exits `0` (idempotent). The connection string can
also be passed as `--connection "<value>"`, which overrides the environment
variable.

#### Locking concurrent runners (CORE-OPS-012)

The migrate-before-API gate (below) orders the runner **before** the API, but it
does **not** stop two migration *runner* invocations from racing each other — a
retried Helm pre-upgrade hook, an overlapping redeploy, or two operator runs can
each start a runner against the same database. Two concurrent runners attempting
the same migration `Up()` (made worse by the retrying execution strategy, which can
re-issue a half-applied migration) could corrupt the schema or the
`__EFMigrationsHistory` table.

To make the step safe under those races, the runner wraps the apply in a
**session-level PostgreSQL advisory lock on a fixed application key**
(`LiveCoreMigrationRunner`): it `pg_advisory_lock`s the key before applying and
releases it after. So a second runner started while the first is mid-apply
**blocks** (it does not error) until the first releases, then finds the schema
already current and applies nothing — both runners exit `0` and the history table
stays consistent, with exactly one applied set. The lock is held on its own
connection for the whole apply; because a session advisory lock is released when
its connection closes, a crashed runner never wedges the lock. This complements the
Helm pre-install/pre-upgrade `Job` (CORE-DEP-004) and the Compose `migrate` gate
(CORE-DEP-001), which order the runner before the API but do not serialise runners.

### Gating the API rollout on the migration step

The rollout must order the runner **before** the API. Use the platform's native
mechanism for a one-shot, run-before primitive:

- **Kubernetes / Helm** — run the migrations image as a pre-install/pre-upgrade
  `Job` (or an init container) and roll the API Deployment only after it
  succeeds. The shipped chart [`deploy/helm/livecore`](../deploy/helm/livecore/README.md)
  does exactly this (CORE-DEP-004; see "In-repo Kubernetes Helm chart" above).
- **Docker Compose** — add a `migrate` service that runs the migrations image to
  completion and make `api` depend on it with
  `depends_on: { migrate: { condition: service_completed_successfully } }`. The
  shipped [`deploy/compose/docker-compose.yml`](../deploy/compose/docker-compose.yml)
  does exactly this (CORE-DEP-001; see "In-repo deployment manifest" above).
- **Railway** — run the migrations image as the service's pre-deploy command so a
  new release applies migrations before the new API instances accept traffic.

### Readiness gates on the schema version (CORE-OBS-010)

The migrate-before-API ordering above is the primary safeguard; the API host also
**defends itself** against an ordering that is skipped, fails, or rolls the API
image forward ahead of its schema. When persistence is configured, `/health/ready`
runs a `database-schema` readiness check that asks EF Core for the migrations the
running build expects that the database lacks
(`GetPendingMigrationsAsync`). If any are missing, the API reports **not-ready**
(`503`) and an orchestrator **leaves it out of rotation** rather than routing
traffic at a host that would `500` on its first domain query; readiness flips back
to ready automatically once the migrations runner has brought the schema up to
date. The check complements — it does not replace — the connectivity check
(connectivity proves the database *answers*; this proves it carries the expected
schema) and the live dependency-reachability probes (CORE-OBS-009). It is bounded
by the database command timeout (CORE-RES-004) and the short readiness timeout
(`HealthChecks:Readiness:ProbeTimeout`; CORE-RES-005) and **fails closed**, while
`/health/live` stays shallow so a stale schema never triggers a restart loop. The
unauthenticated readiness response stays status-only, so which migrations are
missing never leaks (threat T7, `docs/07_SECURITY_THREAT_MODEL.md`).

### Running the migrations without the image

The same apply runs without Docker, against a configured `ConnectionStrings:Database`:

```bash
# The locked apply the runner image performs (the migrate verb takes the
# CORE-OPS-012 advisory lock), for an environment without a container runtime:
ConnectionStrings__Database="Host=localhost;Port=5432;Database=livecore;Username=livecore;Password=..." \
  dotnet run --project apps/api -- migrate

# Or, for local development, apply directly with the pinned dotnet-ef tool. This is
# a developer convenience and does NOT take the advisory lock, so use the migrate
# verb above wherever two runners could race:
dotnet tool restore
ConnectionStrings__Database="Host=localhost;Port=5432;Database=livecore;Username=livecore;Password=..." \
  dotnet ef database update --project apps/api
```

CI proves this path on every change: the `migrations` job
(`.github/workflows/ci.yml`) builds the runner image and applies all migrations
to an empty PostgreSQL database, failing the build if any migration cannot be
applied.

### Migration coverage and model-drift gate (CORE-OPS-002)

The integration test suite defaults to in-memory SQLite with `EnsureCreated()`,
which builds the schema straight from the model and never touches the checked-in
migration files. So that a broken or drifted PostgreSQL migration cannot stay
invisible, the `integration-postgres` CI job (`.github/workflows/ci.yml`) adds two
gates on every change:

- **Real-PostgreSQL migration coverage.** The job spins up a PostgreSQL service
  container and runs the whole integration suite against it. The suite is
  provider-switchable: the `LIVECORE_TEST_DB_PROVIDER=Postgres` and
  `LIVECORE_TEST_POSTGRES` environment variables make each test use a throwaway
  PostgreSQL database whose schema is applied by the **real, checked-in
  migrations** (`Database.Migrate()`), rather than the SQLite `EnsureCreated()`
  schema. The migrations are applied once to a template database and each per-test
  database is a fast copy of it, preserving the suite's per-test isolation. Without
  those environment variables the suite stays on in-memory SQLite, so local runs
  and the default `dotnet test` need no database server.
- **Model-vs-migration drift gate.** The job runs
  `dotnet ef migrations has-pending-model-changes --project apps/api`, which fails
  when the EF Core model has changes not captured in a migration. A change to an
  entity mapping without a matching migration fails CI instead of shipping a schema
  that the model and the migrations disagree on.

### Migration rollback policy: roll-forward-only + restore-from-backup, and expand/contract (CORE-DR-004)

**The chosen policy, stated plainly: this platform is roll-forward-only.** The migrations runner image
(`apps/api/Migrations.Dockerfile`) applies migrations **forward only** — its `migrate` command runs
every pending migration's `Up()` (under the CORE-OPS-012 advisory lock) and exits — and a deployment
**never runs a migration's `Down()` in production**. The backward path for a bad deploy is therefore **not** "run the down migration"; it is, in order:

1. **Roll the application image back, not the schema.** The first response to a bad deploy is to redeploy the
   **previous** released API/worker image (`ghcr.io/<owner>/livecore-api:<previous-version>`, CORE-OPS-009).
   Because every schema change follows the **expand/contract** discipline below, the previous application
   version is still compatible with the new schema, so an application-only rollback is fast, safe and **loses
   no data**. This — not a down migration — is the routine rollback.
2. **Restore from backup only when data was actually lost or corrupted.** If the bad deploy destroyed or
   corrupted data (rather than merely shipping bad code), recover with the **tested restore runbook** (see
   "Backup and restore", CORE-OPS-010 / CORE-DR-001 / CORE-DR-002): restore the encrypted dump and the asset
   mirror, verify every system of record against the backup manifest, apply any pending migrations to the
   restored database, validate `/health/ready`, then cut over. Restoring is the only supported way to undo a
   committed, data-losing change.

**Why `Down()` is not the rollback mechanism.** Every checked-in `Down()` is **destructive**: it drops the
table or column its `Up()` added (for example `AddWorkspaceStatus.Down()` drops the `workspaces.status`
column; the table-creating migrations' `Down()` drops the whole table). Running one in production would
discard committed tenant data and — worse — the **append-only systems of record** (the audit log, the
session-event stream, the purchase ledger) whose loss is unrecoverable (see "Backup and restore"). The
`Down()` methods are kept because EF Core generates them and they are useful for **local development and a
throwaway database** (stepping a migration back on a scratch database, resetting a dev environment); they are
**never** part of a production rollback.

#### Expand/contract (parallel change), so destructive changes are never run blindly

A migration must never, in a single step, drop or rename a column or table that the **currently-running**
application still reads or writes — otherwise a rollback to the previous application version would require the
schema to be rolled back too, which the roll-forward-only policy forbids. Split a destructive change across
releases using the **expand/contract** (parallel-change) pattern:

1. **Expand** — add the new shape in a **backward-compatible** migration: a new **nullable** column (or a
   NOT NULL column with a safe default, as `AddWorkspaceStatus` does), a new table, or a new index. The
   currently-running application keeps working unchanged against it. Deploy an application version that writes
   **both** the old and the new shape and can read either.
2. **Migrate** — backfill existing rows into the new shape and switch reads over to it, still tolerating the
   old shape.
3. **Contract** — only **after** the new application version is fully rolled out and proven, a **later,
   separate** migration drops the now-unused old column/table. This contract migration is the one with a
   destructive effect, and it is applied **forward** as its own deploy — never as a `Down()`. Until contract
   ships, a rollback to the previous application version needs no schema change.

The rule of thumb: **add in one release, remove in a later one.** A column that is added and dropped in the
same release, or a `Down()` relied on to "undo" a deploy, is the anti-pattern this policy exists to prevent.
Two examples already in the tree: `AddWorkspaceStatus` is a safe **expand** (a NOT NULL column with a
back-filled default), and `AddOptimisticConcurrencyTokens` is a deliberate schema-level **no-op** (the `xmin`
system column is mapped in the EF model only) — the safest kind of change, with nothing to roll back.

#### Role separation makes the policy enforceable

The forward migrations are applied by the **more-privileged migration-runner role** (which legitimately needs
DDL and, for a tenant-teardown cascade, `DELETE`), while the runtime application connects as a **least-privilege
role** that cannot `DROP`/`ALTER` schema and has `UPDATE`/`DELETE` **revoked on `audit_logs`** (see
"Audit-log tamper-evidence", CORE-SEC-003). So even an application compromise cannot run a destructive `Down()`
or rewrite history; a deliberate restore uses the migration-runner/owner role.

#### CI lint: a destructive `Down()` is flagged for review

So a new destructive `Down()` cannot merge without a conscious decision under this policy, CI runs a lint
(`scripts/lint-migration-downs.ps1`, the `migration-down-lint` job) that scans every migration class's `Down()`
body and flags any that **drops a table or a column** (the data-destroying operations; index/foreign-key drops
lose no row data and are not flagged). The lint is **not** a prohibition — destructive `Down()`s are expected
and are never run in production — it is an acknowledgement gate: each flagged migration is recorded in
`csv/migration_destructive_down_review.csv` as reviewed under the roll-forward-only policy, and the build fails
when a migration has a destructive `Down()` that is **not** acknowledged (or an acknowledged one's operations
changed, or a baseline row went stale). A reviewer who has confirmed the change follows the expand/contract
guidance acknowledges it with `scripts/lint-migration-downs.ps1 -UpdateBaseline`. The lint logic is covered by
`scripts/test-migration-down-lint.ps1`.

### Audit-log tamper-evidence and tamper-proofing: REVOKE UPDATE/DELETE on `audit_logs` (CORE-SEC-003, CORE-SEC-004)

The append-only `audit_logs` table is **tamper-evident** at the application level: every entry is sealed into a
per-tenant SHA-256 **hash chain** (`sequence` + `previous_hash` + `entry_hash`, see
`docs/10_DATABASE_SCHEMA.md`), and the `AuditLogChainVerifier` routine **detects** any altered, deleted,
reordered or inserted row. Detection is the in-app control; the matching **prevention** lives at the database
role so the audit trail cannot be rewritten silently in the first place.

CORE-SEC-004 makes the log **tamper-proof in code**, not only tamper-evident, so it cannot be mutated from inside
the running process: the audit read paths return non-tracked entities, and the
`AuditLogTamperProtectionInterceptor` (wired on every runtime context) throws and fails closed if any
`SaveChanges` would `UPDATE`/`DELETE` an audit row. The database-role prevention below is the third layer.

#### The REVOKE is now a checked-in migration (CORE-SEC-004)

The `RevokeAuditLogMutationFromRuntimeRole` migration ships the REVOKE so a deployment no longer has to remember
to run it by hand. Because the runtime role's name is deployment-specific, the migration reads it from a custom
database setting, `livecore.audit_log_app_role`, and is a safe **no-op** when that setting is unset (a
single-role dev/CI database, or an operator who has not opted in). To turn it on, name your least-privilege
runtime role once (as the database owner/superuser), then apply migrations as usual:

```sql
-- Name the runtime application role the migration should REVOKE from (replace livecore_app with your role).
ALTER DATABASE livecore SET livecore.audit_log_app_role = 'livecore_app';
```

On its next run the migration REVOKEs `UPDATE`/`DELETE` on `audit_logs` from that role and re-grants the
`INSERT`/`SELECT` the application still needs — equivalent to running, once, as the owner/superuser:

```sql
REVOKE UPDATE, DELETE ON TABLE audit_logs FROM livecore_app;
-- The application still needs INSERT (append) and SELECT (read); keep those:
GRANT  INSERT, SELECT ON TABLE audit_logs TO livecore_app;
```

This is a defence-in-depth pairing:

- The **hash chain** (in Core) detects tampering, including a deletion or an out-of-band edit, and pinpoints the
  first broken entry — useful even if the REVOKE is ever forgotten or a more-privileged role is used.
- The **REVOKE** (in deployment) prevents the application role from altering history at all, which also blunts
  the one weakness of an *unsigned* hash chain: a privileged actor who can write the table directly could
  otherwise recompute the whole chain after a change. Keeping the migrations/owner role (which legitimately
  needs `DELETE` for a tenant teardown cascade) separate from the runtime application role is what makes the
  REVOKE safe to apply.
- The **in-process interceptor and non-tracked reads** (CORE-SEC-004, in Core) stop an in-process regression
  from mutating the table even before the database role is consulted.

Schema migrations are applied by the separate, more-privileged **migration runner** role (see above), not the
runtime application role, so the REVOKE on the application role does not interfere with applying migrations or
with a tenant-teardown cascade. Cryptographically **signing** or externally **anchoring** the chain (to defend
against a fully privileged actor) is a documented follow-up beyond Core's scope.

## Database connection tuning: command/statement timeouts and pool sizing (CORE-RES-004)

A single pathological query must have a **ceiling**. Before this, the shared Npgsql configuration
(`Persistence/LiveCoreNpgsqlOptions.cs`) turned on the retrying execution strategy (CORE-CONC-003) but set **no**
command timeout, so a stuck query ran to the Npgsql default and — because the retrying strategy can re-issue an
operation up to `EnableRetryOnFailure`'s **6** attempts — the worst-case wall-clock, and the pool/thread occupancy
it costs, was effectively unbounded. The fix bounds **each command** at two layers and sizes the **connection pool**,
all configurable with safe defaults, **without changing** the retry behaviour:

- **Client-side `CommandTimeout`** — applied by Core to every runtime `LiveCoreDbContext` (the API host and every
  worker job). The EF Core / Npgsql driver cancels a command that exceeds it, so a stuck query never blocks its
  thread/connection indefinitely and **each retry attempt is itself bounded**. Default **30 seconds**; tune with
  `Persistence:CommandTimeout` (`Persistence__CommandTimeout`, a `TimeSpan`). A non-positive value is rejected at
  startup (a misconfiguration can never silently turn the bound into "no timeout").
- **Server-side `statement_timeout`** — applied by Core on **every connection open** (a `SET statement_timeout`,
  `Persistence/StatementTimeoutConnectionInterceptor.cs`) so PostgreSQL **aborts** the query on its own even if the
  client cancellation is lost (a wedged connection). This is the defence-in-depth backstop the client timeout cannot
  give. Default **30 seconds**; tune with `Persistence:StatementTimeout` (`Persistence__StatementTimeout`, a
  `TimeSpan`); set it to `00:00:00` to disable the server-side ceiling (the client `CommandTimeout` still applies).
  It is applied **only to the runtime contexts**, deliberately **not** to the design-time/migrations context, so a
  long, controlled schema migration (a large index build) is not bounded by the runtime query ceiling.
- **`Maximum Pool Size` (connection pool sizing)** — this is **production connection guidance** set in the
  **connection string**, not a Core code default, because the right value depends on the database's
  `max_connections` and how many API/worker replicas share it. Size it so **all** API and worker replicas together
  stay within `max_connections` (PostgreSQL's default is `100`). Example:
  `Host=…;Username=…;Password=…;Maximum Pool Size=40`. Connection pooling is on by default (`Pooling=true`); do not
  disable it. The bundled compose manifest shows a tuned split (api `40`, worker `20`); a single-VPS deployment with
  one API and one worker stays well under the default `max_connections`.

Together these mean a stuck query is bounded at the client **and** the server, retry can only ever amplify a
**bounded** quantity (at most `MaxRetryCount` × the per-command ceiling, not an unbounded run), and the pool cannot
be exhausted beyond a sized limit. The retrying execution strategy (CORE-CONC-003) is **preserved unchanged** —
a transient failure is still retried, and a non-transient one (including a `statement_timeout` cancellation) still
fails immediately. The timeouts carry no secret (only timespans, threat T7); the only credential, the connection
string, is supplied from configuration as before. This pairs with the DbContext pooling and authz-lookup caching
of CORE-PERF-003.

### DbContext pooling and the authorization-lookup cache (CORE-PERF-003)

To keep the per-request cost low at a high request rate, the API host and every worker job register the
`LiveCoreDbContext` with **DbContext pooling** (`AddDbContextPool`), reusing a pool of contexts instead of
allocating one per request; pooling changes only throughput, and the pool MAXIMUM stays the `Maximum Pool Size`
guidance above. The tenant context resolver's stable lookups (organization-by-slug, user-profile-by-OIDC,
organization-membership) and the per-endpoint workspace membership/role re-queries are additionally served from a
short-TTL, in-process **authorization-lookup cache**. The cache never changes an authorization decision: it caches
only POSITIVE lookups (a denial is always re-checked, fail-closed) and is INVALIDATED on every membership change
(removal, erasure, tenant deletion), so revocation still takes effect on the caller's next request. Both knobs are
deployment policy read from configuration with safe defaults — leave them unset for the documented behaviour, or
set `AuthorizationCache:Enabled=false` to send every authorization lookup straight to the database.

| Setting (config key)            | Env var                         | Default    | Consumer    | Purpose                                                                 |
| ------------------------------- | ------------------------------- | ---------- | ----------- | ----------------------------------------------------------------------- |
| `Persistence:CommandTimeout`    | `Persistence__CommandTimeout`   | `00:00:30` | API, worker | Client-side per-command ceiling (EF Core/Npgsql `CommandTimeout`).      |
| `Persistence:StatementTimeout`  | `Persistence__StatementTimeout` | `00:00:30` | API, worker | Server-side `statement_timeout`; `00:00:00` disables the server ceiling.|
| `Maximum Pool Size` (in `ConnectionStrings:Database`) | within `ConnectionStrings__Database` | Npgsql default (`100`) | API, worker | Connection-pool cap; tune to the database `max_connections` across replicas. |
| `AuthorizationCache:Enabled`    | `AuthorizationCache__Enabled`   | `true`     | API         | Per-request authorization-lookup cache toggle (CORE-PERF-003); `false` forces every lookup to the database. |
| `AuthorizationCache:Ttl`        | `AuthorizationCache__Ttl`       | `00:00:10` | API         | Absolute TTL of a cached authorization lookup; invalidation on membership change is the primary correctness mechanism. |

## Edge posture: CORS, forwarded headers and HTTPS (CORE-OPS-003)

The Core API is meant to sit **behind a reverse proxy / load balancer that
terminates TLS** (docs/02_ARCHITECTURE.md deployment options: a single VPS with a
proxy, Railway, or Kubernetes ingress). Three settings make that posture correct
and safe; all are runtime configuration with **fail-closed defaults**, so nothing
about which sites may call the API or which proxy hop to trust is hardcoded.

### TLS termination

Core does **not** terminate TLS itself; the proxy presents the certificate and
forwards the request to the API over the internal network. The deployment is
responsible for:

- redirecting `http` to `https` **at the proxy** (the edge), and serving the API
  only over `https` publicly;
- forwarding the original scheme/host/client-IP through the standard
  `X-Forwarded-Proto` / `X-Forwarded-Host` / `X-Forwarded-For` headers (see
  "Forwarded headers" below);
- keeping the OIDC discovery over HTTPS — `Authentication:Oidc:RequireHttpsMetadata`
  stays `true` in production (docs/07_SECURITY_THREAT_MODEL.md).

By default the API host adds neither an HTTPS redirect nor an HSTS header, because
in this posture the public HTTPS boundary lives at the proxy; doing it in the app as
well would either double-redirect or fight the proxy. A deployment that runs the API
with **no** terminating proxy — terminating TLS in Kestrel directly — can instead
turn on **app-level** HSTS and HTTPS redirection (see "App-level HSTS and HTTPS
redirection" below, `HttpsSecurity:*`, CORE-SEC-005); both toggles are off by default
so this posture is unchanged.

### OIDC audience is mandatory in Production (CORE-OPS-004)

`Authentication:Oidc:Audience` is the API's expected token audience (the `aud`
claim a token must carry). Audience validation is enabled **only when an Audience is
configured**, so a blank `Audience` silently disables audience scoping — the API
would accept any token the configured issuer (`Authentication:Oidc:Authority`) signs,
including one minted for a different client/application on the same identity provider.

To stop that foot-gun, the `Audience` is **effectively mandatory in production**:

- When an `Authority` **is** configured and `ASPNETCORE_ENVIRONMENT=Production`
  (the default when the variable is unset), a **blank `Audience` is a
  misconfiguration and the host refuses to start** — it never serves a single
  request with audience validation off. Configure
  `Authentication__Oidc__Audience=<your-api-audience>` for any production
  deployment.
- Outside `Production` (a local `Development` run against an `http` Keycloak) a blank
  `Audience` stays tolerated, the same local-development latitude
  `Authentication:Oidc:RequireHttpsMetadata=false` allows.
- The **unconfigured-`Authority`** case is unchanged: with no `Authority` the host
  still starts and every authenticated endpoint fails closed with `401` (the
  fail-closed default scheme), and the audience guard does not apply because no token
  is ever accepted.

### OIDC backchannel timeouts (CORE-RES-005)

When an `Authority` is configured the API makes **outbound HTTP calls** to the
identity provider to fetch and refresh the discovery document and signing keys (JWKS)
it validates tokens against. Those calls are **bounded** so a slow or unreachable
provider **fails fast** instead of stalling token validation — which, with the global
problem-details handler (CORE-RES-001), would otherwise surface to the caller as a
`500`. All three are runtime configuration with short, safe defaults; a
present-but-malformed value is rejected at startup.

| Key                                        | Default    | Purpose                                                                                  |
| ------------------------------------------ | ---------- | ---------------------------------------------------------------------------------------- |
| `Authentication__Oidc__BackchannelTimeout` | `00:00:30` | Per-request timeout on the backchannel HTTP client (shorter than the framework's 60s default), so an unreachable provider's fetch is aborted rather than hanging. |
| `Authentication__Oidc__AutomaticRefreshInterval` | `06:00:00` | How often the cached configuration (discovery + signing keys) is refreshed, so the provider's key rotation is picked up within a bounded window. |
| `Authentication__Oidc__RefreshInterval`    | `00:05:00` | Minimum interval between forced refreshes — the floor that keeps a burst of validation failures from hammering the provider's metadata endpoint. |

The bound **never relaxes validation**: a token whose signing key cannot be fetched
within the timeout is still **rejected** (fail-closed, threats T1/T5), so a slow
dependency is contained without widening access. The unconfigured-`Authority` path has
no backchannel and is unaffected.

### CORS allowed origins (`Cors:AllowedOrigins`)

A browser/PWA front-end served from a different origin than the API (the Next.js
PWA, docs/02_ARCHITECTURE.md) may call the REST API **and** the `/hubs` SignalR
endpoint only from an origin on a configured allow-list. One named policy is
applied to both surfaces.

- Configure it as a list, e.g. the environment variables
  `Cors__AllowedOrigins__0=https://app.example.com`,
  `Cors__AllowedOrigins__1=https://admin.example.com` (or the `Cors:AllowedOrigins`
  array in a settings file). Each entry is a scheme+host[+port] origin with no
  trailing path.
- **Fail-closed default:** with no configured origins **no** cross-origin browser
  client is allowed — a disallowed origin's preflight receives no
  `Access-Control-Allow-Origin` header and the browser blocks the call. For local
  PWA development, set the dev origin (for example `http://localhost:3000`) through
  an environment variable or your own `appsettings.Development.json` / user-secrets;
  the repository ships **no** default origin.
- CORS is a **browser-enforced** boundary layered on top of the OIDC/tenant
  authorization every endpoint already applies — it never widens server-side
  authorization (a non-browser client ignores CORS, and still needs a valid token
  and membership). Because the allow-list is always an explicit set of origins
  (never a wildcard), credentialed requests are permitted, which a browser SignalR
  client needs.

### Forwarded headers (`ForwardedHeaders:KnownProxies` / `:KnownNetworks`)

`UseForwardedHeaders` restores the real client scheme/host/IP from the proxy's
`X-Forwarded-*` headers, but only when the **immediate peer is a trusted proxy** —
otherwise a client could spoof `X-Forwarded-Proto: https` and make the app believe
an insecure request was secure (threat T7).

- **Loopback** is trusted by the framework default (a proxy on the same host works
  with no extra configuration).
- A proxy on another address — a container-network ingress, a managed load
  balancer — must be named explicitly:
  - `ForwardedHeaders__KnownProxies__0=10.0.0.7` for a specific proxy IP, and/or
  - `ForwardedHeaders__KnownNetworks__0=10.0.0.0/8` for a proxy network (CIDR),
    which is the usual case in Kubernetes / Docker where the proxy's pod IP is not
    fixed.
  - `ForwardedHeaders__ForwardLimit=2` raises the trusted-hop count when there is
    more than one proxy in the chain (the default is one).
- With nothing configured, only loopback is trusted, so an arbitrary internet
  client can never spoof the scheme or host.

### App-level HSTS and HTTPS redirection (`HttpsSecurity:*`) (CORE-SEC-005)

In the **default** TLS-terminating reverse-proxy posture the proxy owns the public
HTTPS boundary — the `http`→`https` redirect and the `Strict-Transport-Security`
(HSTS) header — and the app trusts the proxy's forwarded scheme (above), so the API
adds **neither** of its own. Both toggles are therefore **off by default**, and a
proxy-terminated deployment leaves them off (it is **disabled only where the
documented proxy terminates TLS**).

A deployment that runs the API with **no** terminating proxy — terminating TLS in
Kestrel directly — turns them on so it still gets an app-level redirect and HSTS
header. Both are ASP.NET Core's built-in middleware (`UseHttpsRedirection` /
`UseHsts`, part of the shared framework — no new dependency), wired immediately
**after** `UseForwardedHeaders`:

- **HTTPS redirection (`HttpsSecurity:HttpsRedirection:Enabled`).** Redirects an
  insecure `http` request to `https`. Because forwarded headers are restored first,
  behind a trusted terminating proxy the app already sees `https` and the redirect
  **does not fire**, so enabling it never double-redirects or fights the edge.
  - `HttpsSecurity__HttpsRedirection__Enabled=true` turns it on (default `false`).
  - `HttpsSecurity__HttpsRedirection__StatusCode` is the redirect status (default
    `308` permanent; set `307` for a temporary redirect). A value outside `3xx`
    falls back to the default.
  - `HttpsSecurity__HttpsRedirection__Port` pins the target https port. When unset
    the framework resolves it from `HTTPS_PORT`/`ASPNETCORE_HTTPS_PORT` or the
    server's https address; if none can be determined the request passes through
    un-redirected rather than redirecting to an unknown port.
- **HSTS (`HttpsSecurity:Hsts:Enabled`).** Emits the `Strict-Transport-Security`
  response header on a **secure** response, telling the browser to use `https` for
  the configured max-age. The framework's default excluded hosts (loopback —
  `localhost`/`127.0.0.1`/`[::1]`) are left intact, so local development is never
  pinned to `https`.
  - `HttpsSecurity__Hsts__Enabled=true` turns it on (default `false`).
  - `HttpsSecurity__Hsts__MaxAgeDays` is the `max-age` in days (default `365`; a
    non-positive value falls back to the default). One year is the conventional
    production value and the floor for HSTS preload-list eligibility.
  - `HttpsSecurity__Hsts__IncludeSubDomains` / `HttpsSecurity__Hsts__Preload`
    (both default `false`) add the `includeSubDomains` / `preload` directives. Only
    set `Preload=true` once every subdomain is committed to long-lived HTTPS — a
    preload entry is hard to undo.

The header and the redirect carry no tenant/principal/resource detail (threat T7),
and transport security never widens server-side authorization — it is a coarse edge
defense layered on the OIDC/tenant checks every endpoint already enforces, exactly
like CORS and rate limiting.

### Baseline HTTP security response headers (`SecurityHeaders:*`) (CORE-SEC-009)

The API adds three baseline security headers to **every** API, error and
SignalR-negotiate response (a `404`/`406`/`500` Problem Details included), so a
browser handles the JSON responses safely. They are **on by default** — the API
serves only `application/json`/`application/problem+json` and renders **no HTML**,
so the conservative deny-all posture is always safe — and each is individually
configurable:

- **`X-Content-Type-Options: nosniff`** (`SecurityHeaders:ContentTypeOptions:*`)
  stops a browser MIME-sniffing a response into a type the server did not declare.
- **`Referrer-Policy: no-referrer`** (`SecurityHeaders:ReferrerPolicy:*`) keeps the
  request URL (which can carry a resource id) out of the `Referer` of any onward
  navigation.
- **`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`**
  (`SecurityHeaders:ContentSecurityPolicy:*`) is a **deny-all** policy: the API
  renders no HTML, so no script/style source needs allowing, and
  `frame-ancestors 'none'` forbids embedding the responses in a frame — which
  subsumes `X-Frame-Options`, so no separate framing header is added.

Configuration:

- `SecurityHeaders__Enabled=false` turns the whole feature off (default `true`).
- `SecurityHeaders__<Header>__Enabled=false` removes **exactly that** header and
  leaves the others — e.g. `SecurityHeaders__ContentSecurityPolicy__Enabled=false`
  drops only the CSP.
- `SecurityHeaders__<Header>__Value=…` overrides a header's directive; a blank
  value falls back to the documented default rather than emitting an empty header.

Every value is a **static directive string** (never a tenant/principal/resource
value), so the headers leak no tenant/principal detail (threat T7), and — like
CORS, the rate limiter and transport security — they are a coarse browser-facing
defense layered on the OIDC/tenant authorization every endpoint already enforces;
they never widen authorization. This complements the transport headers above
(CORE-SEC-005).

### HTTP response compression (`ResponseCompression:*`) (CORE-PERF-006)

The API compresses its JSON responses with ASP.NET Core's built-in response-compression
middleware (`UseResponseCompression`), **on by default**, so a client that advertises
`Accept-Encoding: br`/`gzip` receives the same list/feed/replay payload compressed —
cutting bandwidth and latency for large sessions and mobile clients. It changes only the
transfer encoding, never the response body:

- **JSON only, Brotli preferred.** Only `application/json` and `application/problem+json`
  are compressed; the Prometheus `/metrics` text, signed-asset redirects and any
  already-compressed/binary payload pass through untouched. Brotli is preferred with gzip
  as the broad-compatibility fallback. A client that sends no `Accept-Encoding` (or only an
  encoding the server cannot satisfy) gets the identical uncompressed body.
- **The SignalR hub transport is excluded.** The middleware is added only for non-hub paths
  (the `/hubs` area), so SignalR's own transport framing is never double-compressed.

Configuration:

- `ResponseCompression__Enabled=false` turns the whole feature off (default `true`): the
  middleware is not added and every response is sent uncompressed.
- `ResponseCompression__EnableForHttps=false` reverts to the framework's HTTPS-off posture
  (default `true`, i.e. JSON is compressed over HTTPS too). HTTPS compression is on by
  default because the API returns bearer-token-authorized JSON **data**, not HTML mixing a
  stable secret with reflected request input, so the BREACH precondition does not apply.

Compression carries no tenant/principal/resource detail (threat T7) and — like CORS, the
rate limiter and the security headers — it is a coarse edge optimization layered on the
OIDC/tenant authorization every endpoint already enforces; it never widens authorization.

### Constrained host header (`AllowedHosts`)

`AllowedHosts` is constrained (no longer `*`): the repository default permits only
`localhost;127.0.0.1`. A deployment **must** set it to its real public host(s),
for example `AllowedHosts=app.example.com` (semicolon-separated for several), so
the host-filtering middleware rejects requests carrying an unexpected `Host`
header.

### Request rate limiting (`RateLimiting:*`) (CORE-SEC-001, CORE-SEC-007)

The API applies ASP.NET Core's built-in rate limiting (`UseRateLimiter`) as
complementary fixed-window limiters, **on by default** with safe, generous limits:

- A **strict per-IP** limit on the anonymous store-notification webhooks
  (`POST /api/v1/store-notifications/{apple,google/rtdn}`). These are
  unauthenticated server-to-server callbacks that do database work and run a
  deployment-supplied parser per call, so they are the primary abuse/DoS surface;
  the per-IP partition uses the **real client IP** restored by `UseForwardedHeaders`
  from a trusted proxy. Defaults: `60` requests per `60` seconds per IP. The
  webhooks additionally have a hard request-body-size cap
  (`RateLimiting__Webhooks__MaxRequestBodyBytes`, default `131072` bytes) — a body
  over the cap is rejected `413` before it is buffered or parsed.
- A **per-principal global** limit on the authenticated surface, partitioned on the
  OIDC issuer+subject pair so one caller's burst cannot exhaust another's allowance.
  Defaults: `300` requests per `60` seconds per principal.
- A **per-IP limit on the anonymous NON-webhook surface** (CORE-SEC-007) — the
  `/hubs/session` SignalR negotiate and any anonymous REST probe — so an
  unauthenticated flood is bounded too. Defaults: `300` requests per `60` seconds
  per IP. Anonymous **infrastructure** traffic (the `/health/*` and `/metrics`
  endpoints) opts out (`DisableRateLimiting`), so orchestrator probes and Prometheus
  scrapes from a single source are never throttled.

- Every limit is runtime configuration: `RateLimiting__Global__PermitLimit` /
  `__WindowSeconds` / `__QueueLimit`, `RateLimiting__Anonymous__PermitLimit` /
  `__WindowSeconds` / `__QueueLimit`, `RateLimiting__Webhooks__PermitLimit` /
  `__WindowSeconds` / `__QueueLimit` / `__MaxRequestBodyBytes`. A non-positive
  value falls back to the default (a misconfiguration never silently removes a
  limit). Setting `RateLimiting__Enabled=false` turns the configurable feature off
  (for a deployment that throttles at its edge instead): the per-principal and
  per-IP anonymous limiters become no-ops, **but** the anonymous webhook keeps a
  **non-disableable request-rate floor** (`RateLimiting__Webhooks__FloorPermitLimit`
  / `__FloorWindowSeconds`, defaults `600` per `60` seconds per IP — a non-positive
  value falls back to the default, so it cannot be configured away) and its body-size
  cap. So disabling the limiter can never fully remove webhook volume protection.
- An excess request gets `429 Too Many Requests` as RFC 7807 Problem Details with a
  `Retry-After` header and no tenant/principal/resource detail (threat T7). Rate
  limiting is a coarse abuse ceiling layered **on top of** the OIDC/tenant
  authorization every endpoint already enforces; it never widens authorization.

## Readiness and worker liveness (CORE-OPS-005)

### Production readiness gate

The API exposes two unauthenticated health endpoints (see the README "Health
endpoints"): `/health/live` (liveness — the process is up, no dependency checks)
and `/health/ready` (readiness — the checks tagged `ready`). Wire them to the
orchestrator: route traffic on `/health/ready`, restart on `/health/live`.

`/health/ready` previously reported `Healthy` whenever no readiness check failed,
and the only such check — database connectivity — is registered **only when a
connection string is configured**. So a host deployed with **no** persistence (or
no OIDC identity provider) reported **READY** even though every domain route then
fails closed (`503` with no persistence, `401` with no identity provider):
orchestration would route live traffic at an API that cannot serve it.

In a **Production** environment (`ASPNETCORE_ENVIRONMENT=Production`, the default
when the variable is unset) readiness now **fails** (`503`) when a required
dependency is unconfigured:

- persistence — `ConnectionStrings:Database`
  (`ConnectionStrings__Database`), and
- OIDC — `Authentication:Oidc:Authority`.

So a misconfigured production host leaves the ready rotation instead of advertising
a readiness it does not have. `/health/live` is **unaffected** (a not-ready
misconfiguration must never trigger a restart of an otherwise live process).
Outside `Production` the gate is **inert** — a `Development` run with no database
or identity provider still reports `Healthy`, the same local-development latitude
the OIDC audience guard grants (CORE-OPS-004). The readiness response stays
**status-only**, so which dependency is missing never leaks to the unauthenticated
endpoint (threat T7). (Audience is separately mandatory in production — a
configured `Authority` with a blank `Audience` refuses to start, see above.)

### Deep dependency reachability (CORE-OBS-009)

The database readiness check is a **live** probe, but the gate above is only a
startup-captured boolean of config **presence**: a host with a valid database but
an OIDC provider, object-storage backend or realtime backplane that is **configured
yet dead** still reported **READY** and took traffic it could not fully serve. The
deep readiness checks close that gap. For each **configured** critical dependency,
`/health/ready` now makes a **live, short-bounded reachability probe** every time it
is evaluated:

| Probe                | Reaches                                                              | Configured by                          |
| -------------------- | ------------------------------------------------------------------- | -------------------------------------- |
| `oidc-discovery`     | `GET {Authority}/.well-known/openid-configuration` answers          | `Authentication:Oidc:Authority`        |
| `object-storage`     | the S3-compatible backend answers an account-level call             | `Assets:Storage:*` (endpoint + creds)  |
| `realtime-backplane` | the Redis/Valkey backplane answers a `PING`                         | `Realtime:Backplane:ConnectionString`  |

So a host whose dependency is configured but unreachable reports **not-ready**
(`503`) and **leaves the rotation** instead of advertising a readiness it does not
have. A dependency that is **not** configured is not probed — the in-process
single-instance backplane and the fail-closed unconfigured storage have nothing
live to reach — so with none configured the deep gate is inert (`Healthy`),
preserving the same local-development latitude. The probes care about
**reachability**, not authorization: a backend that answers with a client error (a
permissions-scoped storage credential, for example) is still reachable and stays
ready; only a transport failure (connection refused, DNS, timeout) is not-ready.

Each probe is bounded by a short, configurable timeout
(`HealthChecks:Readiness:ProbeTimeout` / `HealthChecks__Readiness__ProbeTimeout`,
default `2s`; CORE-RES-005) and **fails closed** — a probe that errors or exceeds
the timeout is counted as not-ready, never as a false Ready, and a slow/hung
dependency can never stall the readiness response. `/health/live` is **unaffected**
(it remains shallow and runs no probes, so a dead dependency never restarts an
otherwise-live process), and the readiness response stays **status-only**, so which
dependency is unreachable never leaks to the unauthenticated endpoint (threat T7).

### Worker metrics and per-loop liveness (CORE-DR-003)

The worker is the host doing **irreversible** work, so it must not be a monitoring
blind spot. It exposes a small HTTP surface (the ASP.NET Core shared framework the
referenced API project already brings — no new dependency), bound to a configurable
listen URL (`Worker:Metrics:Url` / `Worker__Metrics__Url`, default `http://0.0.0.0:9464`):

- **`GET /metrics`** — the Prometheus scrape endpoint, wired exactly as the API host's
  `/metrics` (`docs/15_OBSERVABILITY.md`). It serves the OpenTelemetry-collected
  `LiveCore` series, so the `livecore_job_failures_total` counter each loop records on
  failure (tagged by the coarse `job` name) is actually scrapeable. Like the API's
  `/metrics`, it is **unauthenticated by convention** — a Prometheus server scrapes it
  from inside the deployment network — and a deployment **restricts it at the
  reverse-proxy/network edge**. It carries only low-cardinality aggregates, never
  content (threat T7).
- **`GET /health/live`** — the worker's **per-loop** liveness endpoint. Wire it to the
  orchestrator's liveness probe (restart on failure), exactly as for the API.

The worker runs up to **five** job loops — asset cleanup (`AssetCleanupBackgroundService`),
recap generation (`RecapGenerationBackgroundService`, CORE-JOB-001), export processing
(`ExportProcessingBackgroundService`, CORE-JOB-002), the billing-gated store-notification
reconciliation (`StoreNotificationReconciliationBackgroundService`, CORE-JOB-003) and the
data-retention sweep (`DataRetentionSweepBackgroundService`, CORE-PRIV-003). A loop is
resilient to a sweep that _throws_, but a sweep that **hangs** (a stuck database or storage
call) would leave the process alive yet doing no work.

Each loop writes the current UTC timestamp to its **own** heartbeat file on startup and
after **every completed sweep tick**, and `/health/live` is healthy **only when every
active loop's file is fresh**. Before this, all loops shared **one** file, so a single
healthy loop kept it fresh and **masked** the others hanging; per-loop files plus the
aggregating endpoint make a **single** hung loop detectable.

- Configure the base path with `Worker:Heartbeat:FilePath`
  (`Worker__Heartbeat__FilePath`); the default is `<temp>/livecore-worker.heartbeat`.
  Each loop's file is that base suffixed with the loop name (e.g.
  `…/livecore-worker.heartbeat.asset-cleanup`).
- Configure the staleness threshold with `Worker:Heartbeat:StaleAfter`
  (`Worker__Heartbeat__StaleAfter`, a `TimeSpan`); the default is **2 hours**, a few of
  every loop's default 1-hour sweep interval (`Assets:Cleanup:SweepInterval` /
  `Recaps:Generation:SweepInterval` / `Exports:Processing:SweepInterval` /
  `Store:Reconciliation:SweepInterval` / `Retention:SweepInterval`). A loop whose file is older than this — or
  missing — reads as **stalled** (fail-closed), and the worker reports not-live so
  orchestration restarts it.
- Prefer the HTTP probe (`httpGet: /health/live`), which aggregates all loops in one
  check. An `exec` probe checking a single file's age still works but only covers the
  loop whose file it reads, so it cannot see another loop hanging.
- The liveness check is wired **alongside** the jobs: with **no** database there is no
  loop and liveness is **vacuously healthy** (there is nothing to stall), while
  `/health/live` and `/metrics` still respond — exactly as the API's `/metrics` and
  `/health/*` respond without persistence. A heartbeat write never crashes the worker
  (a transient error is logged and swallowed; a persistent failure makes that loop's
  file go stale, which is fail-safe). It carries only a timestamp — no identifiers, no
  secrets (threat T7).

### Metrics scraping and example alerting/SLOs (CORE-OBS-008)

Both hosts expose a Prometheus scrape endpoint at `GET /metrics` — the API host (CORE-OBS-001) and the worker on
its configurable `Worker:Metrics:Url` (default port `9464`, CORE-DR-003). Both are **unauthenticated by
convention**: a Prometheus server scrapes them from inside the deployment network, and a deployment **restricts
them at the reverse-proxy/network edge**. They carry only low-cardinality aggregate series — no tenant, principal
or resource detail (threat T7).

So an operator is not left with raw metrics and no thresholds, the repository ships **example observability
assets** under [`deploy/observability/`](../deploy/observability/README.md): a Prometheus scrape configuration
(targeting the API and worker `/metrics`), example **recording and alert rules**, **documented SLO targets** for
the `livecore_*` series, and a **starter Grafana dashboard**. They are examples to copy and tune. Point Prometheus
at `deploy/observability/prometheus/prometheus.yml` (or copy its `scrape_configs` and `rule_files` into an existing
Prometheus) and import the dashboard.

The worker tags its `livecore_job_*` series with a `job` attribute naming the loop; under the default
`honor_labels: false` Prometheus renames it to `exported_job` (the target `job` label, e.g. `livecore-worker`,
wins). The full SLO target table, the per-alert thresholds and the CI validation (`promtool check` plus the
consistency gate) are documented in `docs/15_OBSERVABILITY.md` ("Example alert rules, SLO targets and a starter
dashboard") and [`deploy/observability/README.md`](../deploy/observability/README.md). A consolidated
failure-response runbook mapping each signal to operator actions is a documented follow-up (CORE-OPS-014).

### Multi-replica worker safety (CORE-RES-003)

The worker is **horizontally scalable**: you may run more than one replica for availability or throughput, and a
job is never redundantly processed by two of them. Each worker loop is safe under concurrency by a mechanism
matched to its work:

- **Export processing** (`ExportProcessingBackgroundService`) **claims/leases** each job before doing any work.
  A sweep atomically leases a job to this replica — a compare-and-swap that records `export_jobs.lease_owner`
  and a `leased_until` expiry — so a job an unexpired lease is already held on is skipped and two replicas never
  both build one export's manifest. A replica that **crashes mid-job** lets its lease lapse, after which the next
  sweep **reclaims** the job and finishes it, so work is never stranded. Correctness rests on the claim, not
  solely on the downstream unique `export_manifests(export_job_id)` index (kept as a backstop). Tune the lease
  with `Exports:Processing:LeaseDuration` (`Exports__Processing__LeaseDuration`, a `TimeSpan`; default
  **5 minutes**): keep it above the time to process one job, and at or above the sweep interval so a crashed
  lease is reclaimed on the following sweep.
- **Asset cleanup** (`AssetCleanupBackgroundService`) deletes each abandoned upload-intent row **inside the
  per-item guard**, so a concurrent-delete race — another replica removing the same row first — is treated as
  already-removed and never aborts the rest of the batch (object-first-then-row still holds; CORE-RES-003).
- The other loops are concurrency-safe through their own data-layer invariants: **recap generation** admits at
  most one system recap per session through a partial unique index (CORE-RCP-001); the **store-notification
  reconciliation** and **data-retention** sweeps use idempotent, by-id set operations whose overlapping runs
  never double-apply or error.

No replica-affinity, sticky scheduling or external lock service is required — the guarantees live in the database
(`docs/15_OBSERVABILITY.md` covers how the claim/lease is observed). A single-replica worker behaves identically;
the lease is simply never contended.

## Object storage (CORE-OPS-006)

An asset's binary content lives in a **private**, S3-compatible bucket, never in
PostgreSQL (`docs/12_STORAGE_ASSETS.md`; ADR 0006). Core ships a concrete
S3-compatible storage adapter (`S3CompatibleAssetStorage`, over `AWSSDK.S3`) that
mints SigV4 pre-signed upload/download URLs and deletes objects; it is selected
**conditionally** on the configuration below (used by both the API host and the
worker cleanup job). With it unconfigured — or only **partially** configured — the
fail-closed default stays in place and every asset operation returns `503`, so
assets stay private by default even when storage is not configured (threat T4).

Configure it under `Assets:Storage:*` (environment variables shown in
double-underscore form). **No credential lives in the repository** — endpoint and
keys are runtime configuration only (threat T7):

| Key                              | Required | Default     | Purpose                                                              |
| -------------------------------- | -------- | ----------- | ------------------------------------------------------------------- |
| `Assets__Storage__Endpoint`      | yes      | —           | The S3-compatible service endpoint URL.                             |
| `Assets__Storage__AccessKeyId`   | yes      | —           | Access key id used to sign requests.                                |
| `Assets__Storage__SecretAccessKey` | yes    | —           | Secret access key used to sign requests.                            |
| `Assets__Storage__Region`        | no       | `us-east-1` | Region used in the SigV4 signature.                                 |
| `Assets__Storage__ForcePathStyle` | no      | `true`      | Path-style addressing (`endpoint/bucket/key`); needed self-hosted.  |
| `Assets__Storage__UrlLifetime`   | no       | `00:15:00`  | Signed-URL validity window; validated `> 0` and `≤ 1h`.             |
| `Assets__Storage__RequestTimeout` | no      | `00:00:30`  | Per-request SDK `Timeout` so a hung delete fails fast (CORE-RES-005); validated `> 0`. |
| `Assets__Storage__MaxErrorRetry` | no       | `2`         | Bounded SDK retry count (CORE-RES-005); validated `≥ 0`.            |
| `Assets__Storage__RetryMode`     | no       | `Standard`  | Bounded SDK retry mode (`Legacy`/`Standard`/`Adaptive`) (CORE-RES-005). |
| `Assets__Storage__Bucket`        | no       | `livecore-assets` | The private bucket new assets are stored in (per-asset naming). |
| `Assets__Storage__Provider`      | no       | `s3`        | Provider identifier recorded on each asset row (per-asset naming).  |

All three of `Endpoint`, `AccessKeyId` and `SecretAccessKey` must be present for the
concrete adapter to be wired; any one missing keeps the fail-closed default. The
bucket named here must exist on the endpoint and be **private** (no public access,
no public listing). The same configuration drives the worker, so the background
cleanup job can delete the objects of abandoned upload intents.

**Bounded outbound calls (CORE-RES-005).** The only storage operation that makes a
real network round-trip (`DeleteObjectAsync`, used by the worker cleanup/retention
jobs — minting a pre-signed URL is local) is bounded by `RequestTimeout`, `MaxErrorRetry`
and `RetryMode` above, so a hung object-storage backend **fails fast** rather than
blocking a worker thread up to the AWS SDK's 100-second default and amplifying through
retries. Defaults are short and safe; a present-but-invalid value (a non-positive
timeout, a negative retry count or an unrecognised retry mode) is rejected at startup.
A storage failure stays **fail-closed and contained** — these bounds never weaken the
private-by-default posture, they only stop a slow dependency from stalling (threat T4).

Example (a self-hosted RustFS in the local Compose stack):

```bash
Assets__Storage__Endpoint=http://rustfs:9000
Assets__Storage__AccessKeyId=<access-key>
Assets__Storage__SecretAccessKey=<secret-key>
Assets__Storage__Bucket=livecore-assets
Assets__Storage__ForcePathStyle=true
```

## Realtime scale-out backplane (CORE-OPS-007)

Realtime delivery uses SignalR (`docs/11_REALTIME_SYNC.md`). With a single API instance no backplane is
needed. **When more than one API instance runs** (HA or horizontal scale) a **Valkey/Redis-compatible
backplane is required**: SignalR tracks hub group membership per-process, so without a shared backplane an
event computed on one instance reaches only the clients connected to **that** instance and is **silently
dropped** for clients connected to the others.

Core ships the official ASP.NET Core SignalR backplane (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`),
selected **conditionally** on the configuration below. With it configured, every hub group send is published
over Redis pub/sub and reaches the connections held by every instance. Enabling it changes only the
**transport**: the per-recipient recipient computation is unchanged, so the backplane still only transports an
already-authorized, per-recipient delivery to one server-managed group and never widens the audience (threat
T3). With it **unconfigured** the host stays on the in-memory backplane — correct for a **single instance
only** (the documented single-instance constraint).

Configure it under `Realtime:Backplane:*` (environment variables shown in double-underscore form). **No
connection string lives in the repository** — it is runtime configuration only (threat T7):

| Key                                     | Required | Default | Purpose                                                                              |
| --------------------------------------- | -------- | ------- | ------------------------------------------------------------------------------------ |
| `Realtime__Backplane__ConnectionString` | for multi-instance | — | The Redis/Valkey connection string (StackExchange.Redis format). Unset = single-instance in-process backplane. |
| `Realtime__Backplane__ChannelPrefix`    | no       | —       | Namespaces this deployment's SignalR pub/sub channels, so one Redis/Valkey instance can be shared (e.g. backplane + cache) without collisions. |

Example (the self-hosted Valkey in the local Compose stack):

```bash
Realtime__Backplane__ConnectionString=valkey:6379
Realtime__Backplane__ChannelPrefix=livecore
```

A managed/secured server uses the full StackExchange.Redis connection-string form, e.g.
`Realtime__Backplane__ConnectionString="redis.example.com:6380,password=<secret>,ssl=true"`. All API
instances must point at the **same** server (and the same channel prefix) for cross-instance delivery to work.

## Graceful shutdown and SignalR sticky-session affinity (CORE-DEP-002)

### Graceful shutdown drain window (`Hosting:ShutdownTimeout`)

Both hosts **drain their in-flight work on shutdown within a tuned window**, so a **rolling restart does not
abruptly cut an in-flight request**. On a rolling deploy the orchestrator brings up a new instance, waits for it
to pass `/health/ready`, then sends the old instance a termination signal (SIGTERM). The old host stops
accepting new connections and **drains**:

- the **API** lets in-flight HTTP requests and open SignalR connections complete, and
- the **worker** lets each background job loop's current tick observe cancellation and unwind (the loops already
  honor the stopping token — see the job loops in `apps/worker`).

`HostOptions.ShutdownTimeout` bounds that drain. Both hosts set it from configuration
(`Hosting:ShutdownTimeout`, a `TimeSpan`) with a tuned default of **25 seconds**, rather than leaving it at the
implicit framework default — one explicit, configurable, documented window applied identically to the API and
the worker (`apps/api/Hosting/GracefulShutdownConfiguration.cs`, wired by both hosts).

- **Coordinate it with the orchestration grace period.** The drain window must stay **at or below** the
  orchestrator's termination grace period (Kubernetes `terminationGracePeriodSeconds`, default 30s; the Compose
  `stop_grace_period`), or the process is force-killed (SIGKILL) **mid-drain**. The 25-second default is
  deliberately a few seconds under the conventional 30-second grace period so the process exits cleanly before
  SIGKILL. A deployment that needs a longer drain raises **both** in lockstep (for example
  `Hosting__ShutdownTimeout=00:00:50` with `terminationGracePeriodSeconds: 60`).
- **Fail-safe configuration.** A present-but-malformed or non-positive value is rejected at startup rather than
  silently collapsing the window; with nothing configured the safe default applies, so both hosts run without any
  shutdown configuration (the same posture as the worker heartbeat/metrics options).

| Setting (config key)      | Env var                    | Default    | Consumer    | Purpose                                                       |
| ------------------------- | -------------------------- | ---------- | ----------- | ------------------------------------------------------------- |
| `Hosting:ShutdownTimeout` | `Hosting__ShutdownTimeout` | `00:00:25` | API, worker | Drain window for in-flight HTTP/SignalR/job work on shutdown. |

### Multi-instance SignalR requires sticky sessions / ARR affinity

The Redis/Valkey backplane (CORE-OPS-007, above) is **necessary but not sufficient** for a multi-instance
SignalR deployment. A SignalR connection begins with a **negotiate** request that returns a `connectionId` and
the transports the server supports, followed by the actual transport connection. Unless the client negotiates
**WebSockets and only WebSockets** (a single, long-lived connection), the handshake and the non-WebSocket
fallbacks (Server-Sent Events, long polling) make **multiple HTTP requests that must all reach the same server
instance** that issued the `connectionId`. Without affinity a load balancer can route the negotiate and the
follow-up transport requests to **different** instances, and the handshake **breaks** — the second instance has
never heard of that `connectionId`.

So a deployment running **more than one API instance** must enable **sticky sessions** (session affinity / ARR
affinity) at the reverse proxy or load balancer for the `/hubs` SignalR endpoint, **in addition to** configuring
the backplane:

- **Nginx** — a cookie/IP-hash `sticky` upstream (or `ip_hash`).
- **HAProxy** — `cookie SERVERID insert indirect nofollow` with per-server `cookie` values.
- **Kubernetes ingress** — `nginx.ingress.kubernetes.io/affinity: "cookie"` (or the equivalent for your ingress
  controller).
- **Azure App Service / IIS ARR** — ARR affinity (the `ARRAffinity` cookie) enabled.

The two controls solve **different** problems and are both required at scale: **affinity** keeps a single
client's negotiate + transport handshake pinned to one instance, while the **backplane** fans a server-computed
event out to the connections held by **every** instance. Affinity is a deployment/edge concern (a proxy
setting), not a Core host setting; the only way to avoid it entirely is to force WebSockets-only transport and
disable the fallbacks, which is brittle across corporate proxies and is not the default. See
`docs/11_REALTIME_SYNC.md` ("Scale-out").

## Store receipt verification adapter (CORE-MON-008)

Apple/Google receipt verification is **delegated to a deployment-supplied adapter**, exactly like the
S3-compatible `IAssetStorage` (CORE-OPS-006) and the Valkey/Redis `IRealtimeBackplane` (CORE-OPS-007). Core
ships the fail-closed **port** (`IPurchaseVerificationProvider`, CORE-STORE-001) and the verify-then-record
endpoints over it (CORE-STORE-003/004), but **no native store SDK and no provider keys** — the cryptographic
verification needs the deployment's App Store key / Google service-account credentials, which must never live in
this repository (threat T7). A deployment registers one adapter per provider it supports
(`services.AddSingleton<IPurchaseVerificationProvider, MyAppleAdapter>()`); with none registered the resolver
fails closed and every verification request is `503` (no entitlement is ever granted without a real verification
behind it).

### What an adapter MUST guarantee

A conforming `IPurchaseVerificationProvider` adapter owns the cryptographic verification and MUST:

- **Verify the proof against the provider's server APIs** — treat the client-supplied proof as opaque, untrusted
  input; only the provider's confirmation makes it a genuine purchase. Reduce the provider's raw response to a
  provider-neutral `PurchaseVerificationResult` (a normalized `VerifiedPurchase` on success, a generic log-safe
  rejection otherwise). Never log the proof or any receipt content.
- **Report the verified environment.** Set `VerifiedPurchase.Environment` to `Production` or `Sandbox` from the
  verified receipt (Apple's signed transaction carries an `environment`; Google distinguishes a test purchase
  from a live one). Core enforces **sandbox/production separation** with this: a **production** deployment
  (`ASPNETCORE_ENVIRONMENT=Production`) honors only a `Production` purchase and rejects a `Sandbox` one (`422`,
  nothing recorded or granted) — **a sandbox receipt is not honored in production**. If the adapter genuinely
  cannot determine the environment it must report `Sandbox` (the fail-closed default), never `Production`.
- **Reject a replayed receipt.** A proof the provider reports as already consumed/redeemed is not a fresh
  grantable purchase — return it as a `Rejected` result (`422`). Core adds a second, provider-independent layer:
  recording is idempotent on the (`provider`, `provider_transaction_id`) pair, so a replayed-but-genuine proof
  that re-verifies to the same purchase grants nothing twice.
- **Distinguish "not genuine" from "unavailable".** A definitive "not a real purchase" verdict is a `Rejected`
  result; a provider being unreachable/misconfigured is an exception (fail-closed) — so a transient outage is
  never mistaken for a definitive rejection and never silently grants.

### Configuration

The adapter's credentials (Apple/Google server keys, signing keys) are consumed by the **adapter**, not read from
a fixed Core configuration key — supply them to your adapter through your secret store. No provider key lives in
the repository. The host environment that selects the production vs sandbox honoring posture is the standard
`ASPNETCORE_ENVIRONMENT` (the same variable the OIDC audience guard, the readiness gate and the configuration
contract below key off); a production deployment sets it to `Production`.

## Log level and format (CORE-OBS-011)

Both hosts (the API and the worker) emit **structured, single-line JSON** log entries to stdout through the
JSON console formatter built into `Microsoft.Extensions.Logging` (UTC timestamps, scopes included; CORE-FND-004,
CORE-OBS-002) — no external logging dependency. The **format posture is a fixed, safe default**, deliberately
not an operator knob: structured JSON is what makes the logs machine-parseable for a log aggregator and is the
shape the per-request context (CORE-OBS-002) and the ID-only-logging guardrail (CORE-OBS-006) are built on, so a
self-hoster always gets parseable, ID-only logs. What an operator **does** tune is the **verbosity**, through
the standard .NET logging configuration, so production log volume can be raised for an incident or lowered for a
quiet deployment **without rebuilding an image**.

- **`Logging:LogLevel:Default` (`Logging__LogLevel__Default`)** is the **minimum emitted level** for every
  category without a more specific override. The shipped default is **`Information`**; set it to `Debug` (or
  `Trace`) to raise verbosity while diagnosing an incident, or `Warning`/`Error` to quiet a noisy deployment.
  Setting `Logging__LogLevel__Default=Debug` demonstrably lowers the host's minimum emitted level — a `Debug`
  line the host suppresses by default is then emitted.
- **Per-category overrides** (`Logging:LogLevel:<Category>` / `Logging__LogLevel__<Category>`) raise or lower one
  category prefix without touching the rest. The hosts ship two as safe defaults: the API quiets the framework's
  own request logging with `Logging__LogLevel__Microsoft.AspNetCore=Warning`, and the worker keeps the
  host-lifecycle (start/stop) messages at `Logging__LogLevel__Microsoft.Hosting.Lifetime=Information`. A
  deployment can add any other category prefix the same way (for example `Logging__LogLevel__LiveCore=Debug` to
  raise only the platform's own logs).

The levels are `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` and `None`. Like the other host
knobs the keys are read from configuration only and carry no secret (threat T7). The level changes only the log
**volume**, never the **content**: every line stays ID-only — identifiers and metadata, never tokens, PII or
resource content (CORE-OBS-006, threat T7) — at every verbosity, and the JSON format is unchanged.

## Secret management and the configuration contract (CORE-OPS-008)

Core holds **no secret in source**. Every connection string, identity setting and credential is supplied at
runtime as configuration — an environment variable, or a value injected from the deployment's secret store —
and the repository ships only the **names** of those settings, never their values (threat T7 in
docs/07_SECURITY_THREAT_MODEL.md). This is the documented contract a self-hoster fills in.

### The names-only env example

A names-only [`.env.example`](../.env.example) ships at the repository root. Copy it to `.env` and fill in real
values for your deployment; `.env` is git-ignored and `.env.example` carries **names only** (it must never
contain a real secret). `.env.example` is the single, authoritative list of every setting the API and worker
read, grouped by concern and annotated `[secret]` / `[prod-required]`.

.NET reads the hierarchical key `A:B:C` from the environment variable `A__B__C` (double underscore); indexed
lists use a numeric suffix (`A__B__0`). The same names work in an `appsettings.json` file, a container's
environment, a Kubernetes `Secret`/`ConfigMap`, Railway variables or Docker secrets.

### The contract: setting → injection mechanism

| Setting (config key)                | Env var                            | Secret | Required                | Consumer    | Fail-closed default when unset                          |
| ----------------------------------- | ---------------------------------- | :----: | ----------------------- | ----------- | ------------------------------------------------------- |
| `ConnectionStrings:Database`        | `ConnectionStrings__Database`      |  yes   | production              | API, worker | No persistence; domain routes `503`; not-ready in prod (set a tuned `Maximum Pool Size`, CORE-RES-004) |
| `Persistence:CommandTimeout`        | `Persistence__CommandTimeout`      |   no   | no (tunable)            | API, worker | `00:00:30` client-side per-command ceiling (CORE-RES-004) |
| `Persistence:StatementTimeout`      | `Persistence__StatementTimeout`    |   no   | no (tunable)            | API, worker | `00:00:30` server-side `statement_timeout`; `00:00:00` disables it (CORE-RES-004) |
| `AuthorizationCache:Enabled`        | `AuthorizationCache__Enabled`      |   no   | no (tunable)            | API         | `true`; per-request authz-lookup cache on, invalidated on membership change (CORE-PERF-003) |
| `AuthorizationCache:Ttl`            | `AuthorizationCache__Ttl`          |   no   | no (tunable)            | API         | `00:00:10` absolute TTL of a cached authz lookup (CORE-PERF-003) |
| `Authentication:Oidc:Authority`     | `Authentication__Oidc__Authority`  |   no   | production              | API         | Auth disabled; authenticated routes `401`; not-ready    |
| `Authentication:Oidc:Audience`      | `Authentication__Oidc__Audience`   |   no   | production              | API         | Refuses to start once Authority is set (CORE-OPS-004)   |
| `Authentication:Oidc:RequireHttpsMetadata` | `Authentication__Oidc__RequireHttpsMetadata` | no | no (dev only)    | API         | `true` (HTTPS metadata required)                        |
| `Cors:AllowedOrigins:N`             | `Cors__AllowedOrigins__0`          |   no   | for a cross-origin PWA  | API         | No cross-origin browser client allowed                  |
| `ForwardedHeaders:KnownProxies:N` / `:KnownNetworks:N` | `ForwardedHeaders__KnownProxies__0` | no | behind a non-loopback proxy | API | Only loopback is a trusted proxy                  |
| `AllowedHosts`                      | `AllowedHosts`                     |   no   | recommended in prod     | API         | `localhost;127.0.0.1`                                   |
| `HttpsSecurity:HttpsRedirection:Enabled` / `Hsts:Enabled` | `HttpsSecurity__HttpsRedirection__Enabled`, `HttpsSecurity__Hsts__Enabled` | no | only without a TLS-terminating proxy | API | Both OFF: the proxy owns the redirect/HSTS (CORE-SEC-005) |
| `SecurityHeaders:Enabled` / `<Header>:Enabled` / `<Header>:Value` | `SecurityHeaders__Enabled`, `SecurityHeaders__ContentTypeOptions__Enabled`, `SecurityHeaders__ContentSecurityPolicy__Value`, … | no | no (tunable) | API | Baseline headers ON: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, deny-all CSP on every response (CORE-SEC-009) |
| `RateLimiting:Enabled` / `Global:*` / `Anonymous:*` / `Webhooks:*` | `RateLimiting__Enabled`, `RateLimiting__Global__PermitLimit`, `RateLimiting__Anonymous__PermitLimit`, `RateLimiting__Webhooks__FloorPermitLimit`, … | no | no (tunable) | API | Rate limiting ON: 300/60s per principal, 300/60s per anonymous IP, 60/60s per webhook IP; non-disableable webhook floor 600/60s (CORE-SEC-001, CORE-SEC-007) |
| `ResponseCompression:Enabled` / `EnableForHttps` | `ResponseCompression__Enabled`, `ResponseCompression__EnableForHttps` | no | no (tunable) | API | JSON response compression ON (Brotli/gzip), incl. over HTTPS; JSON-only, hub transport excluded; content unchanged (CORE-PERF-006) |
| `Hosting:ShutdownTimeout`           | `Hosting__ShutdownTimeout`         |   no   | no (tunable)            | API, worker | `00:00:25` graceful-shutdown drain window for in-flight HTTP/SignalR/job work (CORE-DEP-002) |
| `Logging:LogLevel:Default` / `:<Category>` | `Logging__LogLevel__Default`, `Logging__LogLevel__Microsoft.AspNetCore`, `Logging__LogLevel__Microsoft.Hosting.Lifetime`, … | no | no (tunable) | API, worker | `Information` minimum emitted level (JSON console format fixed); per-category overrides quiet `Microsoft.AspNetCore` to `Warning` (API) and keep `Microsoft.Hosting.Lifetime` at `Information` (worker); `Debug` raises verbosity (CORE-OBS-011) |
| `Assets:Storage:Endpoint`           | `Assets__Storage__Endpoint`        |   no   | for any media feature   | API, worker | Storage fail-closed; asset ops `503` (CORE-OPS-006)     |
| `Assets:Storage:AccessKeyId`        | `Assets__Storage__AccessKeyId`     |  yes   | for any media feature   | API, worker | Storage fail-closed; asset ops `503`                    |
| `Assets:Storage:SecretAccessKey`    | `Assets__Storage__SecretAccessKey` |  yes   | for any media feature   | API, worker | Storage fail-closed; asset ops `503`                    |
| `Realtime:Backplane:ConnectionString` | `Realtime__Backplane__ConnectionString` | yes | for multi-instance   | API         | In-process backplane (single instance only, CORE-OPS-007) |
| `Tracing:Otlp:Endpoint`             | `Tracing__Otlp__Endpoint`          |   no   | for trace export        | API         | Spans produced but not exported (no collector, CORE-OBS-003) |
| `HealthChecks:Readiness:ProbeTimeout` | `HealthChecks__Readiness__ProbeTimeout` | no | no (tunable)         | API         | `00:00:02` per-probe bound for the live `/health/ready` dependency reachability probes; fail-closed (CORE-OBS-009) |
| `Worker:Heartbeat:FilePath`         | `Worker__Heartbeat__FilePath`      |   no   | no                      | worker      | `<temp>/livecore-worker.heartbeat` (per-loop base path, CORE-DR-003) |
| `Worker:Heartbeat:StaleAfter`       | `Worker__Heartbeat__StaleAfter`    |   no   | no                      | worker      | `02:00:00`; a loop idle longer reads as hung -> worker not-live (CORE-DR-003) |
| `Worker:Metrics:Url`                | `Worker__Metrics__Url`             |   no   | no                      | worker      | `http://0.0.0.0:9464` (worker `/metrics` + `/health/live`, CORE-DR-003) |
| `Recaps:Generation:SweepInterval`   | `Recaps__Generation__SweepInterval` |  no   | no                      | worker      | `01:00:00` (recap generation cadence, CORE-JOB-001)     |
| `Retention:<Family>:Enabled`        | `Retention__<Family>__Enabled`     |   no   | no                      | worker      | data-retention purge per family (`Sessions`/`Recaps`/`Exports` off, `Invitations`/`IdempotencyKeys` on by default, CORE-PRIV-003/006) |
| `Backup:Encryption:Passphrase`      | `Backup__Encryption__Passphrase` (or `Backup__Encryption__PassphraseFile`) | yes | for any backup/restore | backup scripts | Backup/restore refuse to run; nothing is written as plaintext (CORE-DR-001) |

The remaining `Assets:Storage:*` keys (`Region`, `ForcePathStyle`, `UrlLifetime`, `Bucket`, `Provider`),
`Realtime:Backplane:ChannelPrefix`, `Worker:Heartbeat:StaleAfter` and the background-job cadences/batch sizes
(`Assets:Cleanup:PendingRetention`/`SweepInterval`/`BatchSize`, `Recaps:Generation:SweepInterval`/`BatchSize`,
`Exports:Processing:SweepInterval`/`BatchSize`) are optional tuning with safe defaults (see CORE-OPS-006 /
CORE-OPS-007 / CORE-DR-003 above). The billing-gated store-notification reconciliation loop is **off by default**
and fail-closed: it runs only when a deployment sets `Store:Reconciliation:Enabled=true`
(`Store__Reconciliation__Enabled`, with optional `:SweepInterval`/`:BatchSize`), per CORE-JOB-003. The
**data-retention sweep** (CORE-PRIV-003/CORE-PRIV-006) is configured under `Retention:*` — a global
`SweepInterval`/`BatchSize` plus a per-family `Retention:<Family>:Enabled` flag and
`Retention:<Family>:RetentionWindow` for each of `Sessions`, `Recaps`, `Exports`, `Invitations` and
`IdempotencyKeys`. Its purges DELETE personal-data-bearing records (and the unbounded idempotency-key store) past
their window (storage limitation, GDPR Art.5(1)(e)), so the families whose deletion would be surprising —
`Sessions` (and their cascade-removed events/recaps/visibility rules), `Recaps` and completed `Exports` — are
**disabled by default** (enable them per family once you have confirmed the windows fit your retention
obligations), while the clear privacy-hygiene purges are **enabled by default**: the `Invitations` purge (a
terminal invitation's plaintext email, 30-day window) and the `IdempotencyKeys` purge (CORE-PRIV-006 — generic
retry-safety rows deleted by age alone once well past any retry horizon, logged by count not by key value, 30-day
window). The repository-root
[`.env.example`](../.env.example) lists every one of these names. The **store** purchase-verification and notification
credentials (Apple/Google server keys, signing keys) are consumed by the deployment-supplied
verification/notification **adapter**, not read from a fixed Core key; supply them to that adapter through your
secret store, and with no adapter configured store verification and notifications fail closed (`503`).

### Injecting from a secret store

- **Kubernetes / Helm** — put `[secret]` values in a `Secret` and the rest in a `ConfigMap`, and project both
  into the container's environment (`envFrom`). The migrations runner reads the same `ConnectionStrings__Database`.
  The shipped chart ([`deploy/helm/livecore`](../deploy/helm/livecore/README.md), CORE-DEP-004) does this:
  its `config:`/`secrets:` values render into a `ConfigMap`/`Secret` projected into all three workloads, with
  every secret empty by default (supply at install time, or set `secrets.existingSecret` to a `Secret` from your
  secret store).
- **Railway** — set each name as a service variable (Railway stores them encrypted); reference shared secrets
  across the API, worker and migrations services.
- **Docker Compose** — keep secrets in an `.env` file (git-ignored) or Docker secrets, and pass them to the
  `api` and `worker` services with `env_file:` / `environment:`.

### Startup validation: fail loudly when a required production value is missing

The host validates the contract at startup and **reuses the existing fail-closed-when-unconfigured posture**
rather than adding a new one. The pure decision lives in `ProductionConfigurationValidator` and is driven by the
environment:

- **Outside Production** the contract is inert: a local `Development` run with no database or identity provider
  still starts and fails closed, the same latitude the OIDC audience guard (CORE-OPS-004) and the readiness gate
  (CORE-OPS-005) grant.
- **In Production**, when a required value (`ConnectionStrings:Database`, `Authentication:Oidc:Authority`,
  `Authentication:Oidc:Audience`) is missing, the host logs a **loud, named `Critical` startup error** listing
  exactly which settings are unset — and only the **key names**, never the configured values, so a secret is
  never written to the log (threat T7). The process does not crash an otherwise-live host: it stays up, fails
  closed (authenticated routes `401`, persistence-backed routes `503`) and reports **not-ready** (CORE-OPS-005),
  so orchestration never routes traffic at it. The one **hard fail-to-start** case is the security foot-gun where
  an `Authority` is configured but the `Audience` is blank (audience validation silently disabled), which the
  audience guard refuses outright (CORE-OPS-004).

### Startup validation: also reject a present-but-malformed value (CORE-OPS-013)

The contract above checks that a required value is **present**; this checks that the critical values the host
can validate **without any I/O** are **well-formed**, so a value that is set but garbled is caught at startup
rather than at the first request (where it would surface as a `500` or silently misbehave). `FindMalformedCriticalSettings`
on the same `ProductionConfigurationValidator` validates, locally and with no network call:

- `ConnectionStrings:Database` — parses as a PostgreSQL connection string;
- `Authentication:Oidc:Authority` — is an **absolute** `http(s)` URI (the issuer the OIDC handler appends
  `/.well-known/openid-configuration` to);
- each `Cors:AllowedOrigins` entry — is a scheme+host[+port] origin with **no** userinfo, path, query, fragment
  or wildcard (the same shape the CORS allow-list documents above);
- each `ForwardedHeaders:KnownNetworks` entry — is a parseable **CIDR** network.

A present-but-malformed value emits the **same** loud, named `Critical` startup log and **not-ready** posture as
a missing one — and, like the missing-value contract, names **only the key**, never the configured value (threat
T7), and is **inert outside Production** (a `Development` run with a half-finished value still starts and fails
closed). A value that is simply **absent** is the missing-value contract's concern above, not this one, so the
two diagnostics never double-report the same key.

## Release container images (CORE-OPS-009)

A deployment runs the published API and worker images rather than building them on the host. CI publishes them to
the **GitHub Container Registry** (`ghcr.io`) on a release, so a self-hoster pulls a known, immutable version:

```text
ghcr.io/<owner>/livecore-api:<version>
ghcr.io/<owner>/livecore-worker:<version>
```

(The one-shot **migrations runner image** of `apps/api/Migrations.Dockerfile` is built from the same source at the
same release version; see "Database migrations" above for how a rollout gates the API on it.)

### How a release is published

Publishing is triggered **only by a release tag push** — never by a pull request or a branch push, so an
unreviewed or in-progress build is never published. The release tag is a SemVer tag matching the package version
(`docs/23_PACKAGE_VERSIONING.md`):

```bash
git tag -a v1.2.3 -m "Core v1.2.3"
git push origin v1.2.3
```

The tag push runs **every** quality gate (build, tests, format/code-style, the boundary scan, the container
builds/smoke tests, the migrations apply and the integration suite). The `publish` job
(`.github/workflows/ci.yml`) runs **only after all of them pass**, so an image is pushed only from a green build.
It needs no stored registry credential: it authenticates with the workflow's `GITHUB_TOKEN`, which is granted
`packages: write` **for that job only** (the rest of the pipeline keeps a read-only token).

### Immutable, versioned tags

The image tag is the **exact release version** (for example `1.2.3`, or a `1.2.3-rc.1` prerelease) — never a moving
tag such as `latest`. The derivation (`scripts/LiveCoreImageTags.psm1`, run through `scripts/derive-image-tags.ps1`)
is **fail-closed**: only a `v<MAJOR>.<MINOR>.<PATCH>` SemVer tag yields a reference; a branch, a pull request, a
moving tag (`latest`), or a malformed/build-metadata tag is rejected, so the publish path can never produce a
mutable or unversioned tag. Before pushing, the job **refuses to overwrite a tag that already exists** in the
registry, so a shipped version is never silently mutated — a re-publish of an existing version fails the build
instead. Because the tags are immutable and version-pinned, a deployment that references
`ghcr.io/<owner>/livecore-api:1.2.3` always resolves the same image, and a pin to the image **digest**
(`...@sha256:...`, reported on the registry) is exact.

`scripts/test-image-tags.ps1` tests these properties (immutable, versioned, fail-closed off a release tag), and the
`publish-dry-run` CI job exercises the same derivation and build on every push and pull request **without
pushing**, so the publish path is verified continuously, not only at release time.

### Pinned base images, SBOM and vulnerability scan (CORE-DEP-003)

The immutable, versioned **release tag** above fixes what a deployment *pulls*, but it does not by itself fix what the
image is *built from*. Three supply-chain controls close that gap so the layers underneath a published version cannot
silently drift and a known-CVE base image cannot ship:

- **Base images pinned by digest.** All three Dockerfiles (`apps/api/Dockerfile`, `apps/worker/Dockerfile`,
  `apps/api/Migrations.Dockerfile`) pin the .NET SDK and ASP.NET runtime base images by immutable digest
  (`mcr.microsoft.com/dotnet/sdk:10.0@sha256:...` and `aspnet:10.0@sha256:...`), not by the floating `10.0` tag. A
  rebuild therefore always resolves the exact same base layers, and a re-published upstream tag cannot change what a
  release was built on. The readable `:10.0` tag is kept beside the digest only for humans. Bump a digest
  deliberately — resolve the new one with `docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0` (it
  reports the multi-arch manifest-list digest, which Docker resolves to the right architecture) and commit the change
  so it is reviewed.
- **Reproducible NuGet restore (locked mode).** Each .NET project commits a `packages.lock.json` (enabled repository
  wide by `RestorePackagesWithLockFile` in `Directory.Build.props`), and the image builds restore in **locked mode**,
  so the published dependency graph is reproducible and the build fails if the resolved packages ever drift from the
  committed lock file. **CI enforces the same locked restore** over the whole solution before it builds and tests (the
  `dotnet` job runs `dotnet restore LiveCore.slnx --locked-mode`, and the `integration-postgres` job restores in
  locked mode too), so the dependency closure CI exercises is exactly the one a published image restores — the two can
  never silently diverge (CORE-CMP-002). This is also what makes pinning a justified **prerelease** dependency safe:
  the `OpenTelemetry.Exporter.Prometheus.AspNetCore` `-beta` (no stable release exists; see
  `docs/15_OBSERVABILITY.md`) is locked to an exact, content-hashed build and CVE-scanned at publish, so it cannot
  float.
- **SBOM and CVE scan on the publish path.** The publish job builds each image, then — **before any push** — produces a
  CycloneDX **SBOM** and a vulnerability **scan report** for it (with Trivy) and runs the supply-chain gate
  (`scripts/assert-image-scan.ps1`). The gate is **fail-closed**: a **critical** vulnerability (for example a known-CVE
  base image), a missing/empty SBOM, or an unreadable report **fails the publish before the image is pushed**. The
  failing severities are configurable (critical by default). The SBOMs and reports are uploaded as the
  `supply-chain-attestations` build artifact. The existing immutable-tag guard is unchanged: after the gate passes, the
  job still refuses to overwrite an already-published tag before pushing.

The gate decision and the SBOM check are pure logic (`scripts/LiveCoreImageScan.psm1`) tested from seeded fixtures by
`scripts/test-image-scan.ps1`, so "a seeded critical CVE fails the gate" is proven deterministically on every push and
pull request. The `publish-dry-run` job additionally produces a real SBOM and scan report for the dry-run images on
every push/pull request, running the gate in **report-only** mode there so a transient base-image CVE documents itself
without blocking ordinary development — the release publish runs the same gate for real. Cryptographic build
**provenance/attestation** is no longer a follow-up: it is now implemented as keyless cosign signatures and SBOM
attestations (see the next section).

### Signed images and SBOM attestations (CORE-SEC-008)

The SBOM and CVE scan above prove *what is inside* a published image and that it carries no known-critical
vulnerability, but until this control they did not let a self-hoster prove *who built it*: the SBOMs were only an
`actions/upload-artifact` build artifact, never a cryptographic signature or a verifiable attestation bound to the
image in the registry. So a `ghcr.io` image could not be proven to have come from this pipeline. This control closes
that gap with [Sigstore cosign](https://docs.sigstore.dev/), so a published image is **signed** and its SBOM is
attached as a verifiable **attestation**:

- **Keyless signing (no key to manage).** On a release tag, **after the CVE gate passes and the images are pushed**, the
  `publish` job runs `cosign sign` against each published **digest**. Signing is **keyless**: cosign requests a
  short-lived OpenID Connect (OIDC) token from GitHub Actions, Sigstore's **Fulcio** CA issues a throwaway signing
  certificate bound to the workflow identity, and the signature is recorded in the **Rekor** transparency log. There is
  **no private key** to store, rotate or leak. The OIDC token is the only added privilege: `id-token: write` is granted
  to the **`publish` job only** (the workflow default is `contents: read`), so no other job can mint one.
- **The CycloneDX SBOM as an attestation.** The job then runs `cosign attest` to attach the **same CycloneDX SBOM**
  produced for the CVE gate as an in-toto **attestation** (`--type cyclonedx`) bound to the image digest, so the bill of
  materials travels *with* the image in the registry, signed, rather than only as a CI artifact.
- **Verification fails closed in CI.** Before the job finishes it runs `cosign verify` and `cosign verify-attestation`
  against the published digest, asserting the GitHub Actions OIDC **issuer** and a **certificate-identity** matching this
  repository's release workflow, then runs a fail-closed gate (`scripts/assert-image-attestation.ps1`) over the
  verification output — so a missing, empty, wrong-predicate or otherwise unverifiable signature/attestation **fails the
  release**. The `publish-dry-run` job mirrors the whole round-trip on every push/pull request against a **locally-built
  digest** in a throwaway local registry with a throwaway key (no OIDC, **no push**), including a negative check that an
  unsigned image fails verification.

A self-hoster verifies a pulled image themselves (resolve `<digest>` with
`docker buildx imagetools inspect <image>:<tag>`):

```bash
cosign verify \
  --certificate-identity-regexp '^https://github.com/<owner>/<repo>/\.github/workflows/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  <image>@<digest>

cosign verify-attestation --type cyclonedx \
  --certificate-identity-regexp '^https://github.com/<owner>/<repo>/\.github/workflows/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  <image>@<digest>
```

The verification **decision** is pure logic (`scripts/LiveCoreImageAttestation.psm1`) tested from seeded fixtures by
`scripts/test-image-attestation.ps1`, so "a missing or invalid signature / SBOM attestation fails closed" is proven
deterministically on every push and pull request without a registry, a key or a network. cosign is installed from a
pinned release binary, the same way Trivy and promtool are, so **no extra GitHub Action enters the supply chain**. This
is the container-image analogue of the npm build provenance the `@livecore/*` packages carry (CORE-PUB-004,
`docs/23_PACKAGE_VERSIONING.md`).

## Backup and restore (CORE-OPS-010)

The Core holds **systems of record whose loss is unrecoverable**: the tenant-isolated, **append-only** audit
trail, the session-event stream and the store purchase ledger, plus the **private object-storage bucket** that
holds asset binaries. None of this can be reconstructed from elsewhere, so a self-hosted deployment **must** run
a backup and must be able to **prove** a restore recovers every one of these. This section is the documented,
tested procedure; the runnable scripts live under `scripts/`.

### What must be backed up (the systems of record)

The coverage contract is `scripts/LiveCoreBackup.psm1`'s catalog (`Get-LiveCoreSystemOfRecordCatalog`), drawn
from `docs/10_DATABASE_SCHEMA.md` and `csv/database_tables.csv`:

| System of record            | Where         | Append-only | Why it is unrecoverable                                       |
| --------------------------- | ------------- | :---------: | ------------------------------------------------------------- |
| `audit_logs`                | PostgreSQL    |     yes     | The tenant audit trail (member removals, archives, reveals).  |
| `session_events`            | PostgreSQL    |     yes     | The durable session event stream (replayed on reconnect).     |
| `purchase_transactions`     | PostgreSQL    |     no      | Verified store purchases — the server source of premium state.|
| `purchase_events`           | PostgreSQL    |     yes     | The purchase state-change trail (renew/cancel/refund/grace).  |
| `store_notification_events` | PostgreSQL    |     yes     | The handled store-notification ledger (idempotency evidence). |
| `object-storage`            | S3-compatible |     no      | The private asset binaries (never stored in PostgreSQL).      |

All the database tables live in the **one** Core database (`ConnectionStrings:Database`), so a full-database
dump captures every database system of record together with the rest of the tenant data; the object store is
backed up separately. Backups contain tenant data and the audit/purchase records, so treat them as **sensitive**
(see "Security" below).

### PostgreSQL backup

Two complementary strategies; a production deployment should run **both**.

- **Logical dump on a cadence (baseline).** A scheduled `pg_dump` in **custom format** (`--format=custom`, which
  restores selectively with `pg_restore`). Simple, portable across major versions and what
  `scripts/backup-livecore.ps1` runs. Recommended cadence: **nightly**, retained for example 7 daily + 4 weekly +
  3 monthly copies (tune to your RPO and storage budget).

  ```bash
  pg_dump --format=custom --no-password \
    --host "$DB_HOST" --port "$DB_PORT" --username "$DB_USER" --dbname "$DB_NAME" \
    --file "livecore-postgres-$(date -u +%Y%m%dT%H%M%SZ).dump"
  ```

- **Point-in-time recovery (PITR, low RPO).** For a small recovery window, take a periodic **base backup**
  (`pg_basebackup`) and **continuously archive WAL** (`archive_mode = on`, an `archive_command` that copies each
  segment to durable, off-host storage). PITR lets you restore to **any moment** before a failure rather than to
  the last nightly dump, which matters for the append-only ledgers. PITR is a PostgreSQL-server configuration (or
  a managed-Postgres feature); the dump cadence above remains the portable, provider-independent baseline.

The database password is **never** committed and **never** put on the command line: the scripts read it from the
same `ConnectionStrings:Database` value the API and worker use and pass it through the `PGPASSWORD` environment
variable (threat T7).

### Object-storage backup

The private asset bucket (`Assets:Storage:*`, CORE-OPS-006) is backed up by **mirroring** it to a separate,
equally private destination — ideally a different bucket in another region or provider — with whatever
S3-compatible tool the deployment already uses (`aws s3 sync`, MinIO/RustFS `mc mirror`, or `rclone sync`).
Enable **object versioning** and a lifecycle policy on the destination so an overwrite or delete is itself
recoverable. The mirror destination stays **private** — no public access and no public listing — exactly like the
source (threat T4).

```bash
# Example: mirror the private bucket to a backup bucket (keep both private).
aws s3 sync "s3://livecore-assets" "s3://livecore-assets-backup" --delete
```

### The coverage manifest and integrity model

A dump file alone does not prove a restore is good. `scripts/backup-livecore.ps1` therefore writes a
`livecore-backup-manifest.json` next to the dump that records, **for every system of record**, a row count and an
**order-independent SHA-256 content checksum** (for the database tables, a hash over `to_jsonb(row)` for every
row; for the bucket, a hash over the object inventory). The manifest builder is **fail-closed**: it refuses to
write a manifest that does not cover every catalog entry, so a backup can never silently omit the audit,
session-event or purchase records.

On restore, `scripts/restore-livecore.ps1` re-measures the **restored** database and bucket the same way and
verifies them against the manifest. Verification fails closed: a missing or incomplete manifest certifies
nothing, and any system of record that comes back with a different row count or a different content checksum
(a dropped record, or a tampered append-only row) makes the restore **FAIL** with a non-zero exit code rather
than be silently accepted.

### Backup encryption at rest (CORE-DR-001)

The dump and the locally mirrored asset binaries hold the audit trail, the purchase ledger and **all** tenant
data, so the tooling **encrypts them at rest** and **refuses to run without an encryption sink** — it never
writes the most sensitive data as plaintext by default (threats T4/T5/T7). The sink is a self-contained,
dependency-free **AES-256-CBC + HMAC-SHA256** (encrypt-then-MAC) file format keyed by a **PBKDF2-HMAC-SHA256**
derivation of an operator-supplied passphrase; it uses only the .NET base class library, so it runs unchanged on
Windows PowerShell 5.1 and PowerShell 7+ and round-trips in the restore drill with no external tool.

- **Configure the passphrase** from configuration, never from source (threat T7): set
  `Backup__Encryption__Passphrase` (or `Backup__Encryption__PassphraseFile`, a file whose contents are the
  passphrase), or pass `-EncryptionPassphrase` / `-EncryptionPassphraseFile`. With none configured the backup and
  the restore both **fail closed** and do no work.
- **What is encrypted.** The PostgreSQL dump is always encrypted — the artifact left at rest is
  `livecore-postgres-<ts>.dump.enc`, and the plaintext dump is removed the moment it is encrypted. When assets are
  mirrored to a local directory (`-StorageMirrorProgram` with `-AssetMirrorDirectory`), every mirrored binary is
  encrypted in place to a sibling `*.enc` file and the plaintext removed; `-AssetMirrorDirectory` is **required**
  in that case so no tenant asset content is left as plaintext.
- **The manifest stays plaintext.** `livecore-backup-manifest.json` records only row counts and content
  checksums (no tenant content), plus an `encryption` block naming the algorithm applied, so an operator can
  confirm the artifacts were encrypted.
- **Restore decrypts and round-trips.** The restore decrypts the dump to a temporary plaintext file (removed
  immediately after `pg_restore`), decrypts the local mirror only for the duration of the asset sync and
  re-encrypts it afterwards, and **fails closed** on a wrong passphrase or a tampered artifact (the HMAC is
  verified before a single byte is decrypted). A legacy plaintext dump is refused rather than silently restored.
- **Key management.** Keep the passphrase in your secret store (a Railway/host environment variable or a mounted
  secret file), back it up **separately** from the backups, and rotate it by taking a fresh backup under the new
  passphrase — an artifact can only be restored with the passphrase it was written with. A lost passphrase means
  the backup cannot be restored, so protect it as carefully as the database password.

### Backing up

```powershell
pwsh -NoProfile -File scripts/backup-livecore.ps1 `
  -OutputDirectory ./backups `
  -ConnectionString "Host=$env:DB_HOST;Port=5432;Database=livecore;Username=livecore;Password=$env:DB_PASSWORD" `
  -StorageBucket livecore-assets `
  -StorageMirrorProgram aws -StorageMirrorArgument @('s3','sync','s3://livecore-assets','./backups/assets','--delete') `
  -AssetMirrorDirectory ./backups/assets `
  -StorageInventoryProgram aws -StorageInventoryArgument @('s3api','list-objects-v2','--bucket','livecore-assets','--query','Contents[].{k:Key,e:ETag,s:Size}','--output','text') `
  -EncryptionPassphrase $env:Backup__Encryption__Passphrase
```

The script requires object-storage coverage (it fails closed without an inventory command) and an encryption
passphrase (it fails closed without one, CORE-DR-001), runs `pg_dump`, encrypts the dump, mirrors and inventories
the bucket, encrypts the local asset mirror, and writes the encrypted dump (`*.dump.enc`) plus
`livecore-backup-manifest.json` to `-OutputDirectory`. Store the whole output directory — encrypted dump, encrypted
mirrored assets and manifest together — on durable, private storage; the most sensitive data is already encrypted
at rest before it leaves the host.

### Restore runbook (tested)

A restore is a deliberate, ordered procedure. Run it as a **drill** on a schedule (for example monthly) into a
throwaway database, not only during a real incident, so the procedure stays proven.

1. **Provision an empty target.** Create a fresh, empty database (for a recovery, the production database; for a
   drill, a scratch one such as `livecore_restore`). Apply the schema is **not** required first — the dump
   carries it; a `--clean` restore can also drop-and-recreate over an existing database.
2. **Restore PostgreSQL and verify coverage in one step.** Run the restore script, which runs `pg_restore`,
   restores the object store from its mirror, re-measures every system of record and verifies it against the
   manifest:

   ```powershell
   pwsh -NoProfile -File scripts/restore-livecore.ps1 `
     -DumpPath ./backups/livecore-postgres-20260613T000000Z.dump.enc `
     -ManifestPath ./backups/livecore-backup-manifest.json `
     -ConnectionString "Host=$env:DB_HOST;Port=5432;Database=livecore_restore;Username=livecore;Password=$env:DB_PASSWORD" `
     -StorageRestoreProgram aws -StorageRestoreArgument @('s3','sync','./backups/assets','s3://livecore-assets-restore') `
     -AssetMirrorDirectory ./backups/assets `
     -StorageInventoryProgram aws -StorageInventoryArgument @('s3api','list-objects-v2','--bucket','livecore-assets-restore','--query','Contents[].{k:Key,e:ETag,s:Size}','--output','text') `
     -EncryptionPassphrase $env:Backup__Encryption__Passphrase
   ```

   The restore needs the **same passphrase** the backup was written with: it decrypts and integrity-verifies the
   `*.dump.enc` dump before `pg_restore` and decrypts the local asset mirror for the sync, failing closed on a
   wrong passphrase or a tampered artifact (CORE-DR-001).

   A green run prints `Restore verified: every system of record matches the backup manifest`. A non-zero exit
   means the restore is **invalid** — do not cut over to it.
3. **Apply pending migrations.** If the backup predates a schema change, run the migrations runner against the
   restored database (see "Database migrations" above) before pointing the API at it.
4. **Validate readiness.** Point a non-production API/worker at the restored database and confirm `/health/ready`
   is healthy and a few authenticated reads succeed, then cut over.

### The restore drill (the runnable, tested check)

`scripts/test-backup-restore-drill.ps1` is the **verifiably runnable** restore drill. It runs a full
backup → persist → restore → verify round-trip over a fixture that models every system of record, using the
**same** coverage and integrity logic (`scripts/LiveCoreBackup.psm1`) the real backup/restore scripts use, and
asserts the safety property both ways: a faithful restore (even with records re-ordered) is **accepted**, while a
restore that drops a purchase record, tampers an append-only audit record, loses the asset bucket, or is
certified by an incomplete manifest is **rejected fail-closed**. It needs no database or object store, so it runs
anywhere:

```powershell
pwsh -NoProfile -File scripts/test-backup-restore-drill.ps1
```

CI runs it on every push and pull request (the `backup-restore-drill` job), so the procedure cannot regress
silently. The fixture drill proves the coverage/integrity **logic**, but the real scripts invoke
`pg_dump`/`pg_restore`/`psql` and the `to_jsonb` checksum, which a fixture never exercises — so a broken tool
argument could ship green. The **`backup-restore-postgres`** CI job closes that gap (CORE-DR-002): on every push
and pull request it runs the real `scripts/backup-livecore.ps1` and `scripts/restore-livecore.ps1` end to end
against a live PostgreSQL (the same service container the migrations/integration jobs use, with the schema applied
by the real migrations). It seeds every system-of-record table, backs them up, restores into a **fresh** database
and asserts the full backup → restore → integrity round-trip (real `pg_dump`/`pg_restore` + `to_jsonb`
row-count/checksum) passes — and that a restore which lost an append-only audit row is rejected fail-closed. You
can run the same end-to-end round-trip locally against a throwaway PostgreSQL with
`scripts/test-backup-restore-postgres.ps1` (or by running `scripts/backup-livecore.ps1` then
`scripts/restore-livecore.ps1` by hand into a scratch database and bucket).

### Cadence, RPO/RTO and retention

- **Cadence:** nightly logical dump + continuous WAL archiving (PITR) for the database; the asset mirror on the
  same or a tighter cadence.
- **RPO:** the dump cadence (≈ 24h) without PITR, or the WAL-archive interval (minutes) with PITR.
- **RTO:** dominated by `pg_restore` + the asset re-sync; rehearse it in the drill so the real-incident time is
  known.
- **Retention:** keep enough generations to survive a late-detected corruption (for example 7 daily + 4 weekly +
  3 monthly), and test restoring an **old** backup, not only the newest.

### Security

Backups contain tenant data and the audit/purchase systems of record, so they are as sensitive as the live
database (threats T5/T7):

- **Encrypt** backups at rest and in transit. At rest is enforced by the tooling itself (CORE-DR-001): the dump
  and the local asset mirror are encrypted with AES-256-CBC + HMAC-SHA256 and the scripts refuse to run without a
  passphrase (see "Backup encryption at rest" above), so the audit, purchase-ledger and tenant data never land as
  plaintext. In transit, keep the mirror destination **private** (no public access, no public listing) like the
  source bucket and move backups only over TLS (threat T4).
- **Restrict access** to the backup store to the operators who need it; a leaked backup is a tenant-data breach.
- **No secrets in the repository:** the scripts read the database password from configuration and pass it via
  `PGPASSWORD`; object-storage credentials belong to the mirror tool's own environment. Nothing here is
  committed (CORE-OPS-008).
