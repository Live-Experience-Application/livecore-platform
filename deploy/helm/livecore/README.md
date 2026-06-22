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

The chart needs container images your cluster can pull. **All three runtime
images — the API, the worker and the migrations runner — are published to GHCR**
(`ghcr.io/<owner>/livecore-{api,worker,migrations}:<version>`, CORE-OPS-009 /
CORE-OPS-015) and the chart **defaults to those published coordinates**, so a
default install pulls every image and **no manual build/push step is required**.
Pin the released version with `--set image.tag=<version>` (it otherwise falls back
to the chart `appVersion`). If your cluster cannot reach GHCR, override the
per-component `repository`/`image.registry`/tag to your own registry or mirror.

Install, supplying the database connection string and OIDC settings (never commit
real secrets — pass them at install time or via `secrets.existingSecret`):

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

### Database connection pool budget (CORE-DEP-014)

`config.Persistence__MaxPoolSize` (default `20`) bounds the Npgsql connection pool
**per process**, projected to both the API and the worker via the ConfigMap. Npgsql
otherwise defaults an unset pool to **100 connections per process**, so without this
cap scaling `api.replicaCount` (or enabling `autoscaling`) could silently exhaust
PostgreSQL's `max_connections` (default `100`). Keep the budget:

```text
(api.replicaCount + worker.replicaCount) × Persistence__MaxPoolSize  ≤  max_connections
```

with headroom for the transient migrate Job and PostgreSQL's reserved connections.
The shipped defaults — `(1 + 1) × 20 = 40` — sit well under `100`. **When you raise
`api.replicaCount`, re-check this budget**: lower `config.Persistence__MaxPoolSize`
(e.g. `--set config.Persistence__MaxPoolSize=15`) or raise the database's
`max_connections`. An explicit `Maximum Pool Size=<N>` inside
`secrets.ConnectionStrings__Database` overrides this key. See `docs/13`
("Database connection tuning" / "The connection budget across replicas").

## Edge: TLS, ingress and scale-out

- Core does **not** terminate TLS (CORE-OPS-003): terminate it at the `Ingress`
  and forward the original scheme/host/IP. Set
  `config.ForwardedHeaders__KnownNetworks__0` to the ingress controller's pod
  network (CIDR) so the API trusts the `X-Forwarded-*` headers — **required behind a
  load balancer** (CORE-SEC-010, see below).
- Set `config.AllowedHosts` and `config.Cors__AllowedOrigins__0` to your real
  host(s)/origin(s). The host filter is **on by default** (CORE-SEC-010, see below).
- The chart defaults to a **single API replica** (`api.replicaCount: 1`). For
  **more than one API replica**, configure the Redis/Valkey backplane
  (`secrets.Realtime__Backplane__ConnectionString`, CORE-OPS-007). SignalR sticky
  sessions (CORE-DEP-002) are then wired **automatically** on the ingress — see
  "Multi-replica SignalR sticky-session affinity" below.

### Secure-by-default host filter and forwarded headers (CORE-SEC-010)

The chart is **secure by default** on two edge settings that an operator behind a
load balancer must otherwise get right by hand.

**Host-header allow-list (`config.AllowedHosts`).** ASP.NET Core treats an **empty**
`AllowedHosts` as **allow-all** (`*` — host-header validation off). So the `ConfigMap`
**never** renders it empty: it is rendered through the `livecore.allowedHosts` helper
(`templates/_helpers.tpl`) as the host(s) you set in `config.AllowedHosts` **joined with
the loopback hosts** `localhost;127.0.0.1`:

- **Left unset** it renders just `localhost;127.0.0.1` — **non-empty**, so host filtering
  is **on by default** and a default install already rejects an unexpected `Host` header.
- **Set your ingress host(s)** for production traffic (semicolon-separated):

  ```bash
  helm install livecore deploy/helm/livecore \
    --set-string config.AllowedHosts="app.example.com;www.example.com"
  # rendered: AllowedHosts: "app.example.com;www.example.com;localhost;127.0.0.1"
  ```

- The loopback hosts stay in the allow-list because the **kubelet liveness/readiness
  probes hit the dynamic pod IP** — which no static allow-list could name — so the API
  and worker probes send a `Host: localhost` header (`httpGet.httpHeaders` in
  `values.yaml`). Keeping `localhost` allowed means a restrictive, real-host allow-list
  **never rejects the probes**. (`localhost` is also the loopback set the framework
  already excludes from HSTS; admitting it widens nothing — every endpoint still enforces
  the OIDC/tenant authorization.)
- An explicit `config.AllowedHosts="*"` is **refused** (it falls back to the loopback
  hosts), so the chart cannot be coaxed into rendering an allow-all host filter.

**Forwarded headers behind a load balancer (`config.ForwardedHeaders__KnownNetworks__0` /
`__KnownProxies__0`).** `UseForwardedHeaders` restores the **real client IP** from the
proxy's `X-Forwarded-For` only when the immediate peer is a **trusted** proxy. Behind a
load balancer / ingress you **must** name it — the proxy's pod network as a CIDR
(`config.ForwardedHeaders__KnownNetworks__0=10.0.0.0/8`, the usual Kubernetes choice
because the ingress pod IP is not fixed) and/or a fixed proxy IP
(`config.ForwardedHeaders__KnownProxies__0=10.0.0.7`). **Without it the API sees the
proxy's IP as every client's**, so the anonymous **per-IP rate-limit partition collapses
to a single bucket** (`RateLimitingConfiguration`): every anonymous caller shares one
limit, so one client's flood throttles all of them and no caller is isolated (threat T9,
`docs/07_SECURITY_THREAT_MODEL.md`).

Both defaults are **overridable**, and **single-node Compose is unchanged** — the Compose
stack ships its own restrictive `AllowedHosts=localhost;127.0.0.1` default
(`deploy/compose/.env.example`) and this story only closes the Kubernetes/Helm path. See
`docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Constrained host header" and "Forwarded headers").

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

### Multi-replica SignalR sticky-session affinity (CORE-DEP-013)

The backplane is necessary but **not sufficient** for multi-replica SignalR. A SignalR
connection starts with a **negotiate** request that issues a `connectionId`; the
non-WebSocket fallbacks (SSE, long polling) then make follow-up requests that **must all
reach the replica that issued it**. The hub keeps the full transport set (it does not
force WebSockets-only), so each client must be **pinned to one replica** at the ingress
(CORE-DEP-002).

So this is not left to operator discipline, the `Ingress` template renders the nginx
cookie-affinity annotations **automatically** when the `Ingress` is enabled **and**
`api.replicaCount > 1` (`ingress.sessionAffinity`, on by default), in `persistent` mode so
a scale event does not silently re-balance an open connection:

```yaml
nginx.ingress.kubernetes.io/affinity: "cookie"
nginx.ingress.kubernetes.io/affinity-mode: "persistent"
nginx.ingress.kubernetes.io/session-cookie-name: "livecore-affinity"
```

- **The single-replica default is unaffected** — at `api.replicaCount: 1` the `Ingress`
  carries no affinity annotation.
- **Operator `ingress.annotations` are merged and win on a key conflict.** For a non-nginx
  controller, turn off the built-in nginx affinity (`ingress.sessionAffinity.enabled=false`)
  and supply your controller's own affinity annotation through `ingress.annotations`:

  ```bash
  helm install livecore deploy/helm/livecore \
    --set api.replicaCount=2 \
    --set-string secrets.Realtime__Backplane__ConnectionString="valkey:6379" \
    --set ingress.enabled=true \
    --set ingress.sessionAffinity.enabled=false \
    --set-string 'ingress.annotations.<your-controller-affinity-annotation>=...'
  ```

- **Opt out** with `--set ingress.sessionAffinity.enabled=false` when the client forces a
  WebSockets-only transport (no negotiate fallback), the one topology that needs no affinity.

### Optional API autoscaling (CORE-DEP-011)

The chart ships an **opt-in** `HorizontalPodAutoscaler` for the API
([`templates/api-hpa.yaml`](templates/api-hpa.yaml)), **disabled by default**
(`autoscaling.enabled: false`) — a default install renders **no** HPA and the API runs at
`api.replicaCount`. Enabling it is the automated form of the "When to add API replicas"
decision in `docs/13`: the chart renders an `autoscaling/v2` `HorizontalPodAutoscaler` that
**targets the API `Deployment`** and scales it between `autoscaling.minReplicas` and
`autoscaling.maxReplicas` to hold the documented CPU/memory utilization targets. While
autoscaling is enabled the `Deployment` omits its static `replicas` so the HPA owns the
count.

| Value                                          | Default | Meaning                                                        |
| ---------------------------------------------- | ------- | -------------------------------------------------------------- |
| `autoscaling.enabled`                          | `false` | Render the HPA (opt-in).                                       |
| `autoscaling.minReplicas`                      | `2`     | Lower replica bound.                                           |
| `autoscaling.maxReplicas`                      | `5`     | Upper replica bound.                                           |
| `autoscaling.targetCPUUtilizationPercentage`   | `75`    | Target average CPU utilization (% of `requests.cpu`). `null` to drop the CPU metric.    |
| `autoscaling.targetMemoryUtilizationPercentage`| `80`    | Target average memory utilization (% of `requests.memory`). `null` to drop the memory metric. |

**Enabling autoscaling implies the multi-replica realtime backplane requirement
(CORE-DEP-009).** An HPA can scale the API past one pod, and SignalR tracks hub-group
membership per process, so without a shared backplane a reveal computed on one pod is
silently dropped for clients on another (CORE-OPS-007). So the chart **fails to render** when
`autoscaling.enabled` is set and no backplane is configured — exactly the guard
`api.replicaCount > 1` trips — and SignalR sticky sessions (CORE-DEP-002 / CORE-DEP-013) are
also required:

```bash
# Fails fast (autoscaling on, no backplane configured):
helm template livecore deploy/helm/livecore --set autoscaling.enabled=true
#   Error: ... ERROR (CORE-DEP-009): autoscaling.enabled is true but no realtime backplane is configured. ...

# Renders the HPA once the backplane is set (enable the Ingress for the sticky-session affinity):
helm template livecore deploy/helm/livecore \
  --set autoscaling.enabled=true \
  --set-string secrets.Realtime__Backplane__ConnectionString="valkey:6379" \
  --set ingress.enabled=true
```

Utilization targets need the API's `resources.requests` (shipped by default, see above) so the
HPA has a denominator to scale against.

### Voluntary-disruption budget (PodDisruptionBudget, CORE-DEP-012)

A **voluntary disruption** — a node drain (cordon + drain for a kernel patch) or a rolling
node-pool upgrade — evicts pods through the **Eviction API**, which honours
`PodDisruptionBudget`s. Without one, draining a node can evict **every** API replica at once;
at `api.replicaCount: 2` that is a full API outage. The chart ships a `PodDisruptionBudget`
for the API ([`templates/api-pdb.yaml`](templates/api-pdb.yaml)) — and a **symmetric one for
the worker** ([`templates/worker-pdb.yaml`](templates/worker-pdb.yaml)) — that caps how many
pods a voluntary disruption may take down at once, so at least one keeps serving.

It is rendered **only when the component can actually run more than one pod** — for the API
`api.replicaCount > 1` **or** `autoscaling.enabled` (the HPA can scale it past one pod); for
the worker `worker.replicaCount > 1`:

- **The single-replica default renders no PDB.** A PDB over a lone pod would block its node
  from ever draining (a stuck upgrade), so the single-replica path is deliberately left
  without one — the chart is **safe at `replicaCount: 1`**.
- **The default budget is `maxUnavailable: 1`** — a voluntary disruption takes at most one
  pod at a time, so at least `replicaCount - 1` (`>= 1`) stay available.
- **Overridable.** Set an absolute floor with `minAvailable` (a PDB carries exactly one of the
  two, and `minAvailable` wins when both are set), or disable a component's PDB entirely.

| Value                                       | Default | Meaning                                                              |
| ------------------------------------------- | ------- | -------------------------------------------------------------------- |
| `api.podDisruptionBudget.enabled`           | `true`  | Render the API PDB (only when the multi-pod condition holds).        |
| `api.podDisruptionBudget.maxUnavailable`    | `1`     | Max API pods a voluntary disruption may take down at once.           |
| `api.podDisruptionBudget.minAvailable`      | `null`  | Absolute floor; wins over `maxUnavailable` when set.                 |
| `worker.podDisruptionBudget.*`              | as API  | Symmetric worker PDB, rendered when `worker.replicaCount > 1`.       |

```bash
# A two-replica API (the PDB renders automatically); keep at least two pods up instead:
helm install livecore deploy/helm/livecore \
  --set api.replicaCount=3 \
  --set-string secrets.Realtime__Backplane__ConnectionString="valkey:6379" \
  --set api.podDisruptionBudget.minAvailable=2

# Opt out of the API PDB (e.g. an external controller manages disruptions):
helm install livecore deploy/helm/livecore \
  --set api.replicaCount=2 \
  --set-string secrets.Realtime__Backplane__ConnectionString="valkey:6379" \
  --set api.podDisruptionBudget.enabled=false
```

## Validation

The chart is linted and schema-validated in CI (the `helm-chart` job), with a
no-tooling static gate runnable locally:

```bash
# Static validation (no helm/kubeconform needed): asserts the pre-install migrate
# Job hook, the probes, the resource requests/limits ceiling, the ConfigMap/Secret
# split, that no secret is hardcoded, that the multi-replica sticky-session affinity
# annotations are gated on api.replicaCount > 1 (CORE-DEP-013), that the optional
# API HPA is opt-in, targets the API Deployment and implies the backplane (CORE-DEP-011),
# and that the API PodDisruptionBudget selects the API pods and is gated on a multi-replica
# API so the single-replica default ships none (CORE-DEP-012).
pwsh -NoProfile -File scripts/test-helm-chart.ps1

# Full render + schema validation (needs helm + kubeconform):
helm lint deploy/helm/livecore
helm template livecore deploy/helm/livecore | kubeconform -strict -summary
```

See `docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Kubernetes / Helm chart") for the full
contract and the secret-store mapping.
