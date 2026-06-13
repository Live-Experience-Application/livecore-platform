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
a self-applying EF Core migrations bundle that applies every pending migration to
the target database and then exits. It carries no credentials; the connection
string is supplied at run time through the **same** configuration key the API
runtime reads, `ConnectionStrings:Database` (environment variable
`ConnectionStrings__Database`).

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

### Gating the API rollout on the migration step

The rollout must order the runner **before** the API. Use the platform's native
mechanism for a one-shot, run-before primitive:

- **Kubernetes / Helm** — run the migrations image as a pre-install/pre-upgrade
  `Job` (or an init container) and roll the API Deployment only after it
  succeeds.
- **Docker Compose** — add a `migrate` service that runs the migrations image to
  completion and make `api` depend on it with
  `depends_on: { migrate: { condition: service_completed_successfully } }`.
- **Railway** — run the migrations image as the service's pre-deploy command so a
  new release applies migrations before the new API instances accept traffic.

### Running the migrations without the image

The same path runs without Docker for local development and CI, because both use
the pinned `dotnet-ef` tool and the same `ConnectionStrings:Database` resolution:

```bash
dotnet tool restore

# Apply migrations directly to a configured database:
ConnectionStrings__Database="Host=localhost;Port=5432;Database=livecore;Username=livecore;Password=..." \
  dotnet ef database update --project apps/api

# Or build the standalone bundle the image wraps (an executable that applies
# migrations), e.g. for an environment without a container runtime:
dotnet ef migrations bundle --project apps/api --self-contained -r linux-x64 -o ./efbundle
ConnectionStrings__Database="Host=...;Password=..." ./efbundle
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

The API host does not add an HTTPS redirect or HSTS middleware, because the public
HTTPS boundary lives at the proxy; doing it in the app as well would either
double-redirect or fight the proxy. If a deployment ever runs the API with **no**
proxy in front, it must terminate TLS in Kestrel directly — that is a deployment
choice, not a Core default.

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

### Constrained host header (`AllowedHosts`)

`AllowedHosts` is constrained (no longer `*`): the repository default permits only
`localhost;127.0.0.1`. A deployment **must** set it to its real public host(s),
for example `AllowedHosts=app.example.com` (semicolon-separated for several), so
the host-filtering middleware rejects requests carrying an unexpected `Host`
header.

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

### Worker liveness heartbeat

The worker host serves no HTTP traffic and exposes no port, so its liveness signal
is a **heartbeat file** rather than a health port. Each job loop — the asset cleanup
loop (`AssetCleanupBackgroundService`) and the recap generation loop
(`RecapGenerationBackgroundService`, CORE-JOB-001) — writes the current UTC timestamp
to the heartbeat file on startup and after **every completed sweep tick**. A loop is
resilient to a sweep that _throws_, but a sweep that **hangs** (a stuck database or
storage call) would leave the process alive yet doing no work; the file is the worker
process's liveness signal, refreshed whenever a loop makes progress, so a worker whose
loops all hang stops refreshing it and the file goes **stale** — the signal
orchestration uses to restart the wedged worker.

- Configure the path with `Worker:Heartbeat:FilePath`
  (`Worker__Heartbeat__FilePath`); the default is `<temp>/livecore-worker.heartbeat`.
  Point it at a path the orchestration probe can also read (for example a mounted
  volume, or simply the container's own filesystem for an `exec` probe).
- Check **freshness**, not just existence. A liveness probe should restart the
  worker when the file's age exceeds a few sweep intervals
  (`Assets:Cleanup:SweepInterval` / `Recaps:Generation:SweepInterval`, both default 1
  hour) — for example a Kubernetes `livenessProbe` running
  `exec: ["sh","-c","test $(( $(date +%s) - $(stat -c %Y /var/run/livecore/worker.heartbeat) )) -lt 7200"]`.
- The heartbeat is wired **alongside** the jobs, so with **no** database there is no
  loop and no heartbeat (there is nothing to stall). A heartbeat write never crashes
  the worker (a transient error is logged and swallowed; a persistent failure just
  makes the file go stale, which is fail-safe). It carries only a timestamp — no
  identifiers, no secrets (threat T7).

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
| `Assets__Storage__Bucket`        | no       | `livecore-assets` | The private bucket new assets are stored in (per-asset naming). |
| `Assets__Storage__Provider`      | no       | `s3`        | Provider identifier recorded on each asset row (per-asset naming).  |

All three of `Endpoint`, `AccessKeyId` and `SecretAccessKey` must be present for the
concrete adapter to be wired; any one missing keeps the fail-closed default. The
bucket named here must exist on the endpoint and be **private** (no public access,
no public listing). The same configuration drives the worker, so the background
cleanup job can delete the objects of abandoned upload intents.

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
| `ConnectionStrings:Database`        | `ConnectionStrings__Database`      |  yes   | production              | API, worker | No persistence; domain routes `503`; not-ready in prod  |
| `Authentication:Oidc:Authority`     | `Authentication__Oidc__Authority`  |   no   | production              | API         | Auth disabled; authenticated routes `401`; not-ready    |
| `Authentication:Oidc:Audience`      | `Authentication__Oidc__Audience`   |   no   | production              | API         | Refuses to start once Authority is set (CORE-OPS-004)   |
| `Authentication:Oidc:RequireHttpsMetadata` | `Authentication__Oidc__RequireHttpsMetadata` | no | no (dev only)    | API         | `true` (HTTPS metadata required)                        |
| `Cors:AllowedOrigins:N`             | `Cors__AllowedOrigins__0`          |   no   | for a cross-origin PWA  | API         | No cross-origin browser client allowed                  |
| `ForwardedHeaders:KnownProxies:N` / `:KnownNetworks:N` | `ForwardedHeaders__KnownProxies__0` | no | behind a non-loopback proxy | API | Only loopback is a trusted proxy                  |
| `AllowedHosts`                      | `AllowedHosts`                     |   no   | recommended in prod     | API         | `localhost;127.0.0.1`                                   |
| `Assets:Storage:Endpoint`           | `Assets__Storage__Endpoint`        |   no   | for any media feature   | API, worker | Storage fail-closed; asset ops `503` (CORE-OPS-006)     |
| `Assets:Storage:AccessKeyId`        | `Assets__Storage__AccessKeyId`     |  yes   | for any media feature   | API, worker | Storage fail-closed; asset ops `503`                    |
| `Assets:Storage:SecretAccessKey`    | `Assets__Storage__SecretAccessKey` |  yes   | for any media feature   | API, worker | Storage fail-closed; asset ops `503`                    |
| `Realtime:Backplane:ConnectionString` | `Realtime__Backplane__ConnectionString` | yes | for multi-instance   | API         | In-process backplane (single instance only, CORE-OPS-007) |
| `Tracing:Otlp:Endpoint`             | `Tracing__Otlp__Endpoint`          |   no   | for trace export        | API         | Spans produced but not exported (no collector, CORE-OBS-003) |
| `Worker:Heartbeat:FilePath`         | `Worker__Heartbeat__FilePath`      |   no   | no                      | worker      | `<temp>/livecore-worker.heartbeat`                      |
| `Recaps:Generation:SweepInterval`   | `Recaps__Generation__SweepInterval` |  no   | no                      | worker      | `01:00:00` (recap generation cadence, CORE-JOB-001)     |

The remaining `Assets:Storage:*` keys (`Region`, `ForcePathStyle`, `UrlLifetime`, `Bucket`, `Provider`),
`Realtime:Backplane:ChannelPrefix` and the background-job batch sizes (`Assets:Cleanup:BatchSize`,
`Recaps:Generation:BatchSize`, both default 50–100) are optional tuning with safe defaults (see CORE-OPS-006 /
CORE-OPS-007 above). The **store** purchase-verification and notification credentials (Apple/Google server keys, signing
keys) are consumed by the deployment-supplied verification/notification **adapter**, not read from a fixed Core
key; supply them to that adapter through your secret store, and with no adapter configured store verification
and notifications fail closed (`503`).

### Injecting from a secret store

- **Kubernetes / Helm** — put `[secret]` values in a `Secret` and the rest in a `ConfigMap`, and project both
  into the container's environment (`envFrom`). The migrations runner reads the same `ConnectionStrings__Database`.
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

### Backing up

```powershell
pwsh -NoProfile -File scripts/backup-livecore.ps1 `
  -OutputDirectory ./backups `
  -ConnectionString "Host=$env:DB_HOST;Port=5432;Database=livecore;Username=livecore;Password=$env:DB_PASSWORD" `
  -StorageBucket livecore-assets `
  -StorageMirrorProgram aws -StorageMirrorArgument @('s3','sync','s3://livecore-assets','./backups/assets','--delete') `
  -StorageInventoryProgram aws -StorageInventoryArgument @('s3api','list-objects-v2','--bucket','livecore-assets','--query','Contents[].{k:Key,e:ETag,s:Size}','--output','text')
```

The script requires object-storage coverage (it fails closed without an inventory command), runs `pg_dump`,
mirrors and inventories the bucket, and writes the dump plus `livecore-backup-manifest.json` to
`-OutputDirectory`. Store the whole output directory — dump, mirrored assets and manifest together — on durable,
private, encrypted storage.

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
     -DumpPath ./backups/livecore-postgres-20260613T000000Z.dump `
     -ManifestPath ./backups/livecore-backup-manifest.json `
     -ConnectionString "Host=$env:DB_HOST;Port=5432;Database=livecore_restore;Username=livecore;Password=$env:DB_PASSWORD" `
     -StorageRestoreProgram aws -StorageRestoreArgument @('s3','sync','./backups/assets','s3://livecore-assets-restore') `
     -StorageInventoryProgram aws -StorageInventoryArgument @('s3api','list-objects-v2','--bucket','livecore-assets-restore','--query','Contents[].{k:Key,e:ETag,s:Size}','--output','text')
   ```

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
silently. For a stronger, end-to-end drill against real tooling, run `scripts/backup-livecore.ps1` followed by
`scripts/restore-livecore.ps1` into a throwaway PostgreSQL database (the CI integration Postgres or a local
container) and a scratch bucket; a successful `restore-livecore.ps1` run is the real-tool equivalent of the drill.

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

- **Encrypt** backups at rest and in transit, and keep the mirror destination **private** (no public access, no
  public listing) like the source bucket (threat T4).
- **Restrict access** to the backup store to the operators who need it; a leaked backup is a tenant-data breach.
- **No secrets in the repository:** the scripts read the database password from configuration and pass it via
  `PGPASSWORD`; object-storage credentials belong to the mirror tool's own environment. Nothing here is
  committed (CORE-OPS-008).
