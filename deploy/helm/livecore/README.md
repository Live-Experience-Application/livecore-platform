# LiveCore — Kubernetes Helm chart

A Helm chart (CORE-DEP-004) that deploys the Core runtime to Kubernetes — the
**API host + the background worker + a one-shot migrations runner** — as the
"Kubernetes with Helm for larger production" option in
`docs/02_ARCHITECTURE.md` and `docs/13_SELF_HOSTING_REQUIREMENTS.md`.

It mirrors the contract the in-repo Docker Compose stack
([`deploy/compose/docker-compose.yml`](../../compose/docker-compose.yml),
CORE-DEP-001) already enforces:

- the **migrate-before-API gate** — the migrations runner runs to completion as a
  **pre-install/pre-upgrade `Job`** and the API/worker roll out only after it
  succeeds;
- the **documented liveness/readiness probes** wired as `httpGet` probes;
- **all configuration externalized** into a `ConfigMap` (non-secret) and a `Secret`
  (`[secret]` keys) — **no secret is baked into the chart**;
- a **`Service`** for the API and an optional **`Ingress`**.

## Quick start

The chart needs container images your cluster can pull. The published API and
worker images are on GHCR (CORE-OPS-009); the **migrations runner image**
(`apps/api/Migrations.Dockerfile`) must be built and pushed to a registry your
cluster can reach (see `docs/13`, "The migration runner"):

```bash
# From the repository root, build and push the migrations runner image:
docker build -f apps/api/Migrations.Dockerfile -t <registry>/livecore-migrations:<version> .
docker push <registry>/livecore-migrations:<version>
```

Then install, supplying the database connection string and OIDC settings (never
commit real secrets — pass them at install time or via `secrets.existingSecret`):

```bash
helm install livecore deploy/helm/livecore \
  --namespace livecore --create-namespace \
  --set image.tag=<version> \
  --set-string config.Authentication__Oidc__Authority=https://id.example.com/realms/livecore \
  --set-string config.Authentication__Oidc__Audience=livecore-api \
  --set-string config.AllowedHosts=app.example.com \
  --set-string secrets.ConnectionStrings__Database="Host=db;Port=5432;Database=livecore;Username=livecore;Password=<password>;Maximum Pool Size=40"
```

Helm runs the migrate `Job` first; the API and worker Deployments are applied only
after it completes successfully.

## The migrate-before-API gate

The API host **never** applies migrations on startup — unsafe for a multi-replica
rollout where replicas would race to migrate (`docs/13`). Instead the schema is
applied by the one-shot migrations runner, declared as a Helm hook in
[`templates/migrate-job.yaml`](templates/migrate-job.yaml):

```yaml
annotations:
  "helm.sh/hook": pre-install,pre-upgrade
  "helm.sh/hook-weight": "-5"
  "helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded
```

Helm runs a pre-install/pre-upgrade hook **to completion before** it applies the
release's other manifests, and **aborts the release if the hook fails**. So the API
Deployment is created only after the migrations `Job` exits `0` — the Helm
equivalent of the Compose `depends_on: { migrate: { condition:
service_completed_successfully } }` gate. The runner is idempotent (an up-to-date
database applies nothing and exits `0`). The API additionally defends itself with a
schema-version readiness check (CORE-OBS-010), so a skipped migration still leaves
it out of rotation.

## Health, readiness and liveness probes

| Component | Probe       | Endpoint        | Purpose                                                  |
| --------- | ----------- | --------------- | ------------------------------------------------------- |
| api       | liveness    | `/health/live`  | Restart on failure.                                     |
| api       | readiness   | `/health/ready` | Route traffic only while passing (CORE-OPS-005).        |
| worker    | liveness    | `/health/live`  | Per-loop heartbeat liveness (CORE-DR-003).              |

The worker's `metrics` port (`9464`) also serves `GET /metrics` for Prometheus. The
runtime images ship no in-container HTTP client and define no `HEALTHCHECK`;
Kubernetes probes them over HTTP, exactly as the image design intends.

## Resource requests and limits

Every workload ships a **non-empty `resources.requests` and `resources.limits`** by
default (CORE-DEP-007 / CORE-DEP-010), so the resource-ceiling guarantee holds on
Kubernetes exactly as the Compose `deploy.resources.limits` enforce it on a single
VPS — a runaway pod cannot starve the node. The numbers **mirror the Compose
baseline** (`deploy/compose/docker-compose.yml`): `requests` reserve the `docs/13`
"minimum (small/idle)" sizing for scheduling, and `limits` cap at the "recommended"
ceiling (the shipped Compose default).

| Component (chart key) | `requests` (cpu / memory) | `limits` (cpu / memory) |
| --------------------- | ------------------------- | ----------------------- |
| migrate (`migrations`) | `250m` / `256Mi`         | `500m` / `512Mi`        |
| API (`api`)            | `500m` / `512Mi`         | `1000m` / `1024Mi`      |
| worker (`worker`)      | `250m` / `384Mi`         | `750m` / `768Mi`        |

PostgreSQL is sized by Compose only — the chart deploys no database (operators bring a
managed database or their own StatefulSet), so just `migrate`/`api`/`worker` are sized
here. The ceilings are **overridable per field** (set `<component>.resources` with
`-f`/`--set`); set `<component>.resources={}` to remove a ceiling entirely:

```bash
# Raise the API memory limit and the worker CPU request:
helm install livecore deploy/helm/livecore \
  --set api.resources.limits.memory=2048Mi \
  --set worker.resources.requests.cpu=500m
```

See `docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Container resource limits and capacity
sizing") for the full sizing baseline.

## Configuration — ConfigMap, Secret, no baked secrets

Every setting is the documented configuration contract (CORE-OPS-008,
`docs/13` / `.env.example`), split into two maps in `values.yaml`:

- `config:` — non-secret keys (OIDC authority/audience, `AllowedHosts`, CORS,
  forwarded-headers network, storage endpoint, ...) rendered into a **ConfigMap**.
- `secrets:` — `[secret]` keys (`ConnectionStrings__Database`, the
  `Assets__Storage__*` credentials, the realtime backplane connection string)
  rendered into a **Secret** (`type: Opaque`, `stringData`).

Both are projected into the migrations runner, the API and the worker with
`envFrom`, so all three read the same `ConnectionStrings__Database` and the rest of
the contract.

**No secret is committed.** Every `secrets.*` value defaults to empty in
`values.yaml`; supply real values at install time (`--set-string` / `-f`), or set
`secrets.existingSecret` to the name of a `Secret` you manage out of band (an
external secret store, a sealed secret) — then the chart renders no `Secret` of its
own and projects yours instead.

Add any other documented key (`.env.example`) under `config:` / `secrets:`; the
templates render whatever keys you supply.

## Edge: TLS, ingress and scale-out

- Core does **not** terminate TLS (CORE-OPS-003): terminate it at the `Ingress`
  and forward the original scheme/host/IP. Set
  `config.ForwardedHeaders__KnownNetworks__0` to the ingress controller's pod
  network (CIDR) so the API trusts the `X-Forwarded-*` headers.
- Set `config.AllowedHosts` and `config.Cors__AllowedOrigins__0` to your real
  host(s)/origin(s).
- The chart defaults to a **single API replica** (`api.replicaCount: 1`). For
  **more than one API replica**, configure the Redis/Valkey backplane
  (`secrets.Realtime__Backplane__ConnectionString`, CORE-OPS-007) **and** enable
  SignalR sticky sessions on the ingress (CORE-DEP-002), e.g.
  `ingress.annotations."nginx.ingress.kubernetes.io/affinity"="cookie"`.

### Multi-replica realtime fail-safe (CORE-DEP-009)

SignalR tracks hub-group membership **per process**, so running more than one API
replica on the in-process backplane silently drops cross-pod realtime delivery: a
reveal computed on one pod never reaches clients connected to another (CORE-OPS-007).
To stop a default install shipping that broken topology, the chart **fails to render**
when `api.replicaCount` is greater than `1` and no realtime backplane is configured:

```bash
# Fails fast (no backplane configured):
helm template livecore deploy/helm/livecore --set api.replicaCount=2
#   Error: ... ERROR (CORE-DEP-009): api.replicaCount is 2 but no realtime backplane is configured. ...

# Renders cleanly once the backplane is set:
helm template livecore deploy/helm/livecore --set api.replicaCount=2 \
  --set-string secrets.Realtime__Backplane__ConnectionString="valkey:6379"
```

When the configuration is projected from an `existingSecret` the chart cannot inspect
its contents, so the guard **defers** to it (the render succeeds) and `NOTES.txt`
prints a prominent reminder that the Secret must carry
`Realtime__Backplane__ConnectionString` and that sticky sessions must be enabled. The
app additionally fails fast at startup when run multi-instance without a backplane
(CORE-RES-006), as defence in depth.

## Validation

The chart is linted and schema-validated in CI (the `helm-chart` job), with a
no-tooling static gate runnable locally:

```bash
# Static validation (no helm/kubeconform needed): asserts the pre-install migrate
# Job hook, the probes, the resource requests/limits ceiling, the ConfigMap/Secret
# split and that no secret is hardcoded.
pwsh -NoProfile -File scripts/test-helm-chart.ps1

# Full render + schema validation (needs helm + kubeconform):
helm lint deploy/helm/livecore
helm template livecore deploy/helm/livecore | kubeconform -strict -summary
```

See `docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Kubernetes / Helm chart") for the full
contract and the secret-store mapping.
