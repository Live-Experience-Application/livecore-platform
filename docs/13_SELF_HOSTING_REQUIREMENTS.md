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
