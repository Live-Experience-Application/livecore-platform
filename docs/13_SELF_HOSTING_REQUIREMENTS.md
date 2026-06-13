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
is a **heartbeat file** rather than a health port. The asset cleanup loop
(`AssetCleanupBackgroundService`) writes the current UTC timestamp to the heartbeat
file on startup and after **every completed sweep tick**. The loop is resilient to a
sweep that _throws_, but a sweep that **hangs** (a stuck database or storage call)
would leave the process alive yet doing no work; because the file is refreshed only
by the loop making progress, a hung sweep stops refreshing it and the file goes
**stale** — the signal orchestration uses to restart the wedged worker.

- Configure the path with `Worker:Heartbeat:FilePath`
  (`Worker__Heartbeat__FilePath`); the default is `<temp>/livecore-worker.heartbeat`.
  Point it at a path the orchestration probe can also read (for example a mounted
  volume, or simply the container's own filesystem for an `exec` probe).
- Check **freshness**, not just existence. A liveness probe should restart the
  worker when the file's age exceeds a few sweep intervals
  (`Assets:Cleanup:SweepInterval`, default 1 hour) — for example a Kubernetes
  `livenessProbe` running
  `exec: ["sh","-c","test $(( $(date +%s) - $(stat -c %Y /var/run/livecore/worker.heartbeat) )) -lt 7200"]`.
- The heartbeat is wired **alongside** the cleanup job, so with **no** database
  there is no loop and no heartbeat (there is nothing to stall). A heartbeat write
  never crashes the worker (a transient error is logged and swallowed; a persistent
  failure just makes the file go stale, which is fail-safe). It carries only a
  timestamp — no identifiers, no secrets (threat T7).
