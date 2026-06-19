# Observability

## Logging

Use structured logs.

Log IDs and event types, not sensitive content bodies.

Required log context:

```text
request_id
organization_id
workspace_id
session_id when applicable
user_id when applicable
event_id when applicable
```

### Implementation (CORE-OBS-002)

JSON logging is wired with `IncludeScopes` (CORE-FND-004), but nothing populated the per-request context
above. CORE-OBS-002 adds it. A single request-scoped owner, `RequestLogContext`
(`apps/api/Observability/`), holds the documented identifiers as their exact snake_case keys. The
`RequestLogContextMiddleware` opens **one** logging scope around the request with that object as the scope
state, so the JSON console formatter renders the populated identifiers on **every** log line the request
emits (including a request-summary line the middleware logs at completion). Because the scope state is mutable
and the formatter enumerates it when it writes each entry, a key set partway through the request appears on
the log lines that follow — so a request's log lines converge on the full applicable context.

The keys are populated by the **authoritative owner** of each identifier, never duplicated:

| Key               | Set by                          | Source                                                        |
| ----------------- | ------------------------------- | ------------------------------------------------------------- |
| `request_id`      | `RequestLogContextMiddleware`   | the per-request correlation id (CORE-OBS-005: a well-formed inbound `X-Request-Id`, else the active trace id, else `HttpContext.TraceIdentifier`) |
| `user_id`         | `RequestLogContextMiddleware`   | the authenticated principal's OIDC issuer-local subject        |
| `workspace_id`    | `RequestLogContextMiddleware`   | the matched route value (a surrogate `Guid` only)              |
| `session_id`      | `RequestLogContextMiddleware`   | the matched route value (a surrogate `Guid` only)              |
| `organization_id` | `TenantContextResolver`         | the resolved tenant (set only on a successful resolution)      |
| `event_id`        | `SessionEventPublisher`         | the published session event's id                               |

The middleware runs **after authentication and before authorization**, so the principal is available to seed
`user_id` while the scope still wraps the authorization step and the endpoint — a fail-closed `401`/`403` is
logged with its `request_id` too. It is **fail-safe**: an anonymous or unmappable caller carries no `user_id`,
a denied (foreign) tenant resolution logs no `organization_id`, and only a well-formed surrogate `Guid` route
value is taken as `workspace_id`/`session_id` (so a free-form path segment can never become a log value). The
unauthenticated infrastructure endpoints (`/health/*`, `/metrics`) are skipped — they carry no tenant or
principal context and are polled frequently.

The context carries **only identifiers and authorization metadata** — opaque surrogate ids and the
principal's subject — never the access token, the display name, the email or any resource content, so the log
surface cannot leak sensitive content (threat T7 in `docs/07_SECURITY_THREAT_MODEL.md`). No external logging
dependency is added; this is the built-in JSON console formatter plus scope enrichment.

### Enforcing ID-only logging as a guardrail (CORE-OBS-006)

"Log IDs and event types, not sensitive content bodies" was a convention the current logs follow but nothing
mechanically enforced — a future content-bearing log statement could ship and only a reviewer would catch it.
CORE-OBS-006 turns the convention into a **build guardrail**, enforced like the boundary scan and the
destructive-migration lint (a PowerShell analysis module + a CI lint + a gate-logic test), so it needs no new
runtime dependency and no `Microsoft.Extensions.Compliance.Redaction`/`[LogProperties]` layer.

The `log-redaction` CI job runs `scripts/test-log-redaction.ps1` (the gate-logic test) and then
`scripts/lint-log-redaction.ps1` (`scripts/LiveCoreLogRedaction.psm1` is the analysis) over the tracked
first-party `apps/` C# source. The lint **fails the build** on an `ILogger` call whose message template would
put a value into the log text rather than a structured identifier:

- an **interpolated** template (`$"...{x}..."`), which embeds the value in the message text and cannot be
  redacted (the CA2254 anti-pattern);
- a template that **concatenates a non-literal** expression into the message (`"user " + name + " ..."`); a
  constant `"a" + "b"` literal join is a normal multi-line template and is allowed;
- a constant template naming a **content/PII/secret** structured property (`{Email}`, `{DisplayName}`,
  `{AccessToken}`, `{ContentBody}`, ...).

Identifier/metadata placeholders (`{ExportJobId}`, `{ItemCount}`, `{ResourceType}`, `{OrganizationSlug}`,
`{ContentBlockId}`) and coarse non-PII names (`{Provider}`, `{JobName}`, `{RequestRoute}`, `{Reason}`) are
unaffected, so every existing ID-only log keeps passing while a new content-bearing one cannot ship. The
analysis is C#-aware (it masks comments and string content before locating calls, so a logger call in a comment
or a string is never mistaken for one and a multi-line template is handled). See the threat model
(`docs/07_SECURITY_THREAT_MODEL.md`, "Log redaction enforced as a guardrail (CORE-OBS-006)") and the README
("Log-redaction guardrail"). This complements the request log context (CORE-OBS-002 above), which already
carries identifiers only.

## Metrics

Track:

- API request duration
- API error rate
- realtime connections
- reveal command latency
- event delivery failures
- asset upload/download failures
- database query failures
- background job failures

### Implementation (CORE-OBS-001)

The eight signals above are implemented with OpenTelemetry metrics over the vendor-neutral
`System.Diagnostics.Metrics` API. A single owner, `LiveCoreMetrics` (`apps/api/Observability/`), defines one
meter named `LiveCore` carrying all eight instruments (plus the five CORE-OBS-007 service-level indicators
below — they REUSE the same meter), and the existing seams record onto it: a request
middleware (request duration + error rate), the realtime hub (connections), the reveal endpoint (reveal
latency), the session-event publisher (event-delivery failures), a transparent `IAssetStorage` decorator
(asset upload/download failures), an EF Core command interceptor (database failures) and the worker's
background jobs (job failures, tagged by a coarse `job` name — one per loop: `asset-cleanup`,
`recap-generation`, `export-processing`, `store-notification-reconciliation` and `data-retention`).

The API host exposes a **Prometheus scrape endpoint** at `GET /metrics` (the OpenTelemetry Prometheus
exporter). It is registered unconditionally — like the health endpoints, it needs no database or identity
provider. The instruments and their exported Prometheus series:

| Signal                       | Instrument                          | Kind          | Exported series (prefix)              |
| ---------------------------- | ----------------------------------- | ------------- | ------------------------------------- |
| API request duration         | `livecore.api.request.duration`     | histogram (s) | `livecore_api_request_duration_seconds` |
| API error rate               | `livecore.api.request.errors`       | counter       | `livecore_api_request_errors_total`   |
| Auth-failure rate (SLI)      | `livecore.api.auth.failures`        | counter       | `livecore_api_auth_failures_total`    |
| Rate-limit rejections (SLI)  | `livecore.api.rate_limit.rejections`| counter       | `livecore_api_rate_limit_rejections_total` |
| Realtime connections         | `livecore.realtime.connections`     | up/down gauge | `livecore_realtime_connections`       |
| Reveal command latency       | `livecore.reveal.duration`          | histogram (s) | `livecore_reveal_duration_seconds`    |
| Event delivery failures      | `livecore.event.delivery.failures`  | counter       | `livecore_event_delivery_failures_total` |
| Asset upload/download failures| `livecore.asset.failures`          | counter       | `livecore_asset_failures_total`       |
| Database query failures      | `livecore.database.failures`        | counter       | `livecore_database_failures_total`    |
| Background job failures      | `livecore.job.failures`             | counter       | `livecore_job_failures_total`         |
| Background job successes (SLI)| `livecore.job.successes`           | counter       | `livecore_job_successes_total`        |
| Background job duration (SLI)| `livecore.job.duration`             | histogram (s) | `livecore_job_duration_seconds`       |
| Background job backlog (SLI) | `livecore.job.backlog`              | gauge         | `livecore_job_backlog`                |

Dimensions are kept **low-cardinality and non-sensitive** (threat T7): the request duration is tagged with
the HTTP method, the route **template** (never the concrete path, so no resource id becomes a label) and the
status code; the others carry only a coarse `operation`/`job` name. The error counter increments only for
server errors (5xx); the fail-closed 401/403/404 the authorization model returns by design are client-side
statuses and are not counted as errors.

#### Service-level indicators (CORE-OBS-007)

The eight instruments above leave three key service-level indicators uncovered: the error counter increments
only on `5xx`, so an **auth-failure spike** (a brute-force or token-replay attempt, a misconfigured client
hammering `401`/`403`) is counted nowhere; the `429` rate-limit path records no metric, so a **rate-limit
storm** is invisible; and the worker records only `job.failures`, so a loop whose queue **backs up** (or whose
sweeps slow down) does so with every existing metric staying green. CORE-OBS-007 adds five SLIs on the SAME
`LiveCore` meter (no new meter, no new dependency) so an operator can alert on each:

- **Auth-failure rate** (`livecore.api.auth.failures`) and **rate-limit rejections**
  (`livecore.api.rate_limit.rejections`) are recorded by the SAME request-metrics middleware that records the
  duration/error signals (`RequestMetricsMiddleware` → `LiveCoreMetrics.RecordApiRequest`). The status is
  classified into exactly one SLI counter: `5xx` → error, `401`/`403` → auth-failure, `429` → rate-limit. They
  carry the SAME low-cardinality tags as the duration (method, route **template**, status code) — the status
  tag distinguishes `401` from `403` — and never a tenant, principal or resource label. Because the middleware
  wraps the whole pipeline (it sits OUTSIDE the rate limiter and the authorization step), a fail-closed
  `401`/`403` and a limiter `429` flow back through it and are counted exactly once, without any new seam.
- **Per-loop worker job successes** (`livecore.job.successes`, the counterpart to `job.failures`),
  **duration** (`livecore.job.duration`, a histogram in seconds) and **backlog/queue depth**
  (`livecore.job.backlog`, a gauge) are recorded by each of the worker's background loops on its existing sweep
  path, tagged only by the coarse `job` name. A completed sweep records one success, the sweep duration and the
  pending-item count it observed (its `examined` count) as the backlog gauge; a failed sweep records the
  failure and the duration up to the throw. The backlog gauge **saturating at the batch size sweep after
  sweep** is the "worker falling behind" signal — the success/failure ratio and the duration trend round out
  the per-loop health. No tenant, principal or content is ever attached (threat T7).

#### Best-effort realtime delivery (CORE-RES-001)

The `livecore_event_delivery_failures_total` counter is the signal that makes the **best-effort** delivery
contract observable. The durable, append-only session event is the source of truth: a reveal/hide and a
session start/end append it **inside** the command's unit-of-work transaction and deliver it **after** the
commit (commit-then-publish, CORE-CONC-002), and the participant join/leave path appends it before
delivering. So once the state is committed a realtime push is a strictly best-effort extra — a reconnecting
client replays a missed event from the durable stream (CORE-RT-005).

The publisher (`SessionEventPublisher.DeliverAsync`) makes that genuine: a backplane (Redis/Valkey) transport
failure is **recorded on this counter and SWALLOWED, not rethrown** (the swallow is per delivery, so one
recipient group's transport hiccup never suppresses the deliveries to the others). A committed
reveal/hide/session/participant operation therefore **still returns success during a backplane outage** —
the live push is counted-and-dropped, never escalated into a `500` (a genuine cancellation is left to
propagate). Before this story the publisher rethrew on any backplane failure and the commit-then-publish
callers awaited it with no `try`/`catch`, so a backplane outage surfaced as a `500` on an
already-committed operation — contradicting the best-effort design; CORE-RES-001 reconciles the code with it.
The metric counts only — no event content is ever attached to it (threat T7).

The `/metrics` endpoint is **unauthenticated by convention** — a Prometheus server scrapes it from inside the
deployment network, exactly as orchestration probes the unauthenticated `/health/*` endpoints — and carries
only aggregate series, never content. A deployment restricts it at the reverse-proxy/network edge
(`docs/13_SELF_HOSTING_REQUIREMENTS.md`).

#### Prerelease exporter justification (CORE-CMP-002)

The Prometheus scrape endpoint uses `OpenTelemetry.Exporter.Prometheus.AspNetCore`, pinned to a **prerelease**
(`1.16.0-beta.1`) alongside its stable `1.16.0` OpenTelemetry siblings. That `-beta` in a release build is
deliberate and justified per `AGENTS.md` (whose "no new dependencies without explicit justification" rule
applies equally to keeping a prerelease pin): the OpenTelemetry .NET Prometheus exporter has **no stable
release**. It ships on the same version train as the stable SDK but keeps the prerelease suffix because the
upstream OpenTelemetry-to-Prometheus exposition specification is not yet declared stable, so the suffix
reflects that spec's status rather than instability of the package — it is the OpenTelemetry-maintained
exporter. It cannot be replaced with a stable release without dropping the mandated `/metrics` endpoint: the
only alternative, `OpenTelemetry.Exporter.Prometheus.HttpListener`, is equally prerelease, so there is no
stable substitute. The supply-chain risk a prerelease would otherwise carry is contained — the exact build is
pinned and restored in **locked mode** against the committed `packages.lock.json`, CI verifies that same
locked restore over the whole solution so the closure cannot float, and the release images are SBOM- and
CVE-scanned before publish (`docs/13_SELF_HOSTING_REQUIREMENTS.md`, CORE-DEP-003).

The prerelease use is **tracked as an explicit dated, owner-named decision with an upgrade trigger** rather than
left as a silent `-beta` (CORE-OBS-004): as of **2026-06-18** every published exporter version is a prerelease
and the latest is the pinned `1.16.0-beta.1` itself, so there is no stable release to upgrade to. The decision
(owner: **Core-later** — the Core platform / observability maintainers) is to keep the pin and **revisit it when
the first STABLE `OpenTelemetry.Exporter.Prometheus.AspNetCore` is published**; the `/metrics` endpoint behavior
is unchanged (it reuses the existing `AddLiveCorePrometheusMetrics` wiring). The pinned version and the upgrade
trigger are asserted by `tests/LiveCore.Api.UnitTests/Observability/PrometheusExporterPinTests.cs`, so a silent
bump fails the build. The full dated decision is recorded in `docs/24_SPEC_CONSISTENCY.md` ("Prerelease
Prometheus exporter pin tracked as a dated decision (CORE-OBS-004)").

The background **worker** records job failures — and, since CORE-OBS-007, the per-loop success count, sweep
duration and backlog/queue depth SLIs — onto the same `LiveCore` meter from each of its job loops (asset
cleanup, recap generation, export processing, the billing-gated store-notification reconciliation and the
data-retention sweep), and it exposes its OWN Prometheus scrape endpoint at `GET /metrics` (CORE-DR-003), wired
exactly as the API host wires it (`AddLiveCorePrometheusMetrics` + `MapLiveCoreMetricsEndpoint`) — so the
`livecore_job_failures_total` counters each loop records on failure are actually scrapeable, not recorded onto
an unobserved meter. The worker binds the surface on a configurable listen URL (`Worker:Metrics:Url`, default
port 9464) and, like the API's `/metrics`, it is unauthenticated by convention and restricted at the network
edge, carrying only low-cardinality aggregates (threat T7). An OTLP push exporter remains a configuration
follow-up — the instruments are export-agnostic.

The worker also serves a per-loop `GET /health/live` endpoint (CORE-DR-003) backed by the same surface; see
`docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Worker liveness heartbeat").

#### Worker max-attempt and dead-letter visibility (CORE-RES-002)

Each export-processing job carries a durable **attempt counter** (`export_jobs.attempt_count`). When a job's
processing transaction fails, the worker records the failed attempt in its own unit of work — so the count is
durable even though the work rolled back — and retries it on the next sweep. Once the count reaches the
configured maximum (`Exports:Processing:MaxAttempts`, default 5) the worker **dead-letters** the job: it drives
it to the terminal `Failed` state via `ExportJob.Fail()` with a generic, content-free reason instead of
re-attempting it forever. A dead-lettered job is terminal, so it drops out of the queued read — it stops
re-consuming a batch slot every sweep, and newer work is no longer starved behind a permanently-failing
("poison") low-id job.

Dead-lettering is observable without a new metric (the dedicated dead-letter **metric** is `CORE-OBS-007`):

- the job reaches the terminal `Failed` status with a generic failure reason on its `export_jobs` row, so a
  broken export surfaces as **failed** rather than staying `Pending` forever — to its requester the export
  read route (`GET /api/v1/exports/{id}`) returns a distinct `409` that says the export *failed* (disclosed
  only after authorization, so the state never leaks to an unauthorized caller; threats T1/T5/T7/T8);
- the worker emits an identifier-only **WARNING** log when it dead-letters a job (the job id, the attempt count
  and the configured maximum — never the requester or any content, threat T7);
- the export-processing sweep summary log counts how many jobs were `dead-lettered` this run alongside
  `processed` and `failed`.

The same bounded-retry/dead-letter posture is intended to generalize to the other worker loops; `CORE-OBS-007`
adds the dead-letter metric and worker backlog/duration SLIs on top of this.

#### Worker job claim/lease visibility (CORE-RES-003)

Export processing is safe to run on **multiple worker replicas**: before doing any work a sweep **atomically
claims** each job by leasing it (`export_jobs.lease_owner` + `export_jobs.leased_until`), a compare-and-swap that
leases the row to exactly one replica, so two replicas never both do the full work of one export and a replica
that crashes mid-job lets its lease **expire** so the next sweep reclaims and finishes the job. Correctness rests
on the claim, not solely on the downstream unique `export_manifests(export_job_id)` index (still a backstop). The
lease duration is `Exports:Processing:LeaseDuration` (default 5 minutes; keep it above the time to process one
job, and at or above the sweep interval so a crashed lease is reclaimed on the following sweep).

The claim is observable without a new metric:

- the lease lives on the `export_jobs` row (`lease_owner`, an opaque per-replica id — never a tenant, a user or
  content, threat T7; `leased_until`), so the replica currently processing a job, and a stale (crashed) lease, are
  both visible in the row;
- the export-processing **sweep summary** log counts `examined` separately from `processed`/`failed`/
  `dead-lettered`, so a gap (jobs examined but skipped because another replica still holds their lease) is
  visible as routine multi-replica contention rather than an error;
- a sweep that **throws** still records the `livecore_job_failures_total` counter (`job=export-processing`) and
  logs identifiers only — the claim adds no new failure surface.

### Authorization-lookup cache and DbContext pooling (CORE-PERF-003)

The per-request authorization-lookup cache and DbContext pooling (docs/02_ARCHITECTURE.md) are request-path
performance optimizations, **observable through the existing signals** and adding **no new telemetry surface**:

- they reduce the number of database round-trips a request makes (the resolver's organization/profile/membership
  lookups and the per-endpoint role re-queries are served from the cache within the TTL), so their effect shows up
  in the existing **API request duration** and **database query failures** signals — there is deliberately no
  separate cache-hit metric;
- the cache is an in-process structure holding only surrogate identifiers and authorization metadata (an
  organization id, a subject id, a role) — never content, a token or a tenant name — so it leaks nothing to logs,
  metrics or traces (threat T7), exactly like the lease owner above;
- it never changes an authorization decision (positive-only, invalidated on every membership change), so the
  fail-closed `401`/`403`/`404` paths are still counted and logged exactly as before — caching adds no failure or
  decision surface to observe.

### Example alert rules, SLO targets and a starter dashboard (CORE-OBS-008)

The instruments above expose the right series, but a self-hoster scraping
`/metrics` still faces raw metrics with **no thresholds** — nothing that says what
"good" looks like or what to alert on. CORE-OBS-008 closes that gap by shipping,
under [`deploy/observability/`](../deploy/observability/README.md), example
Prometheus recording/alert rules, a scrape configuration, **documented SLO
targets** for the `livecore_*` series and a starter Grafana dashboard, so an
operator gets actionable alerting and SLO tracking out of the box. They are
**examples to copy and tune**, not a managed monitoring stack.

| Asset                                                   | Purpose                                                                       |
| ------------------------------------------------------- | ----------------------------------------------------------------------------- |
| `deploy/observability/prometheus/prometheus.yml`        | Example scrape config: scrapes the API (`:8080`) and worker (`:9464`) `/metrics`, loads the rules. |
| `deploy/observability/prometheus/rules/livecore.rules.yml` | Recording rules (the SLO burn series) and alert rules over the `livecore_*` metrics. |
| `deploy/observability/grafana/dashboards/livecore-overview.json` | Starter dashboard: API SLOs, auth/rate-limit signals, dependency failures and worker-loop health. |

#### Scrape guidance

The API and worker each serve `GET /metrics` (the worker on `Worker:Metrics:Url`,
default port `9464`; CORE-DR-003). Both are **unauthenticated by convention**,
scraped from inside the deployment network, and restricted at the
reverse-proxy/network edge (`docs/13_SELF_HOSTING_REQUIREMENTS.md`). The example
config's targets match the in-repo Docker Compose service names/ports
(`deploy/compose/docker-compose.yml`). The recording rules pre-compute each SLO
burn series so a threshold is expressed once and evaluated cheaply.

**Worker loop label.** The worker tags its `livecore_job_*` series with a
low-cardinality `job` attribute naming the loop (`asset-cleanup`,
`recap-generation`, `export-processing`, `store-notification-reconciliation`,
`data-retention`). When Prometheus scrapes, its **own** target `job` label
(`livecore-worker`) wins and the exposed loop label is renamed to **`exported_job`**
(the default `honor_labels: false`), so the worker rules and dashboard panels group
by `exported_job`.

#### Documented SLO targets

The example rules encode these starter targets (tune them to your traffic). Each
is computed from the documented `livecore_*` series via the named recording rule:

| Objective                   | Recording-rule series                                | Starter target        | Alert(s)                                                |
| --------------------------- | ---------------------------------------------------- | --------------------- | ------------------------------------------------------ |
| API availability            | `job:livecore_api_request_error_ratio:rate5m`        | 5xx ratio < 1%        | `LiveCoreApiErrorRatioHigh` (warn), `…Critical` (>5%)  |
| API latency                 | `job:livecore_api_request_duration_seconds:p95_5m`   | p95 < 1s              | `LiveCoreApiLatencyP95High` (warn)                     |
| Auth-failure rate           | `job:livecore_api_auth_failures:rate5m`              | < 1/s sustained       | `LiveCoreApiAuthFailureSpike` (warn)                   |
| Rate-limit rejection rate   | `job:livecore_api_rate_limit_rejections:rate5m`      | < 1/s sustained       | `LiveCoreApiRateLimitRejectionsHigh` (warn)           |
| Dependency failures         | `livecore_database_failures_total`, `livecore_event_delivery_failures_total`, `livecore_asset_failures_total` | none sustained | `LiveCoreDatabaseFailures`, `LiveCoreEventDeliveryFailures`, `LiveCoreAssetFailures` (warn) |
| Worker loop success ratio   | `exported_job:livecore_job_success_ratio:rate15m`    | >= 90%                | `LiveCoreWorkerJobFailing` (warn)                      |
| Worker backlog drains       | `livecore_job_backlog`                              | returns to 0 hourly   | `LiveCoreWorkerBacklogNotDraining` (warn)             |

A worker loop that **hangs** rather than fails is caught by the per-loop
`/health/live` heartbeat (CORE-DR-003), not by a metric alert; wire both. Event
delivery is **best-effort** (the durable event is persisted and replayed on
reconnect; CORE-RES-001), so its alert is a sustained-rate warning, not a hard
failure.

#### Validated in CI, kept consistent with this doc

The `observability-assets` CI job (`.github/workflows/ci.yml`) validates the assets
two ways: `promtool check config`/`promtool check rules` (the canonical Prometheus
validator) over the scrape config and the rules, and
`scripts/test-observability-assets.ps1` (`scripts/LiveCoreObservabilityAssets.psm1`)
which lints the dashboard JSON, checks each alert carries a severity and an
annotation, and asserts **every `livecore_*` series the rules and dashboard
reference is a series documented in the metrics table above or a recording rule
defined in the rules file** — so the rules, the dashboard and this document cannot
silently drift. The gate-logic test proves itself over fixtures (an undocumented
series reference, an alert with no severity and a malformed dashboard JSON are each
rejected) before guarding the real files.

### Realtime hub capacity baseline (CORE-PERF-009)

The HA/scaling guidance (`docs/13_SELF_HOSTING_REQUIREMENTS.md`, "When to add API
replicas and the realtime backplane") used to say "add a replica when concurrent
connection counts outgrow one process" with **no measured number** behind the
claim — an SRE gap before launch. CORE-PERF-009 closes it with a **documented,
repeatable load/soak harness** that drives the SignalR realtime hub to a target
concurrency, **measures the capacity baseline from the realtime metrics already on
the `LiveCore` meter** (no new instrument, no new shipped dependency), and **asserts
the recorded baseline** so a regression below it fails the run.

**The harness.** `tests/LiveCore.Api.IntegrationTests/RealtimeHubLoadBaselineTests.cs`
boots **N API instances** sharing one PostgreSQL system of record and a Redis/Valkey
SignalR backplane under one channel prefix — the same backplane-backed
multi-instance topology the cross-instance correctness suite uses (CORE-TST-003), and
the production HA shape (CORE-DEP-009 backplane, CORE-DEP-013 affinity, CORE-RES-006
fail-fast, CORE-RES-008 eviction). It opens the target number of authenticated hub
connections, distributed across the instances, then drives audience-wide reveal/event
fan-out rounds from a host on one instance — so each reveal must fan out **across the
backplane** to the connections held by the others. The load is driven by the
test-only `Microsoft.AspNetCore.SignalR.Client` (the same client the eviction/delivery
suites use), so it **adds no shipped runtime dependency**.

**The measurement source is the existing realtime metrics** (the signals in the table
above), captured with a `MeterListener` scoped to each instance's own
`LiveCoreMetrics` meter reference (so a concurrently-running host's measurements are
never captured):

| Baseline dimension          | Default baseline (the recorded floor/ceiling) | Measurement source                                                        |
| --------------------------- | --------------------------------------------- | ------------------------------------------------------------------------- |
| Target concurrency          | ≥ 50 connections **per instance** (× 2 instances = 100 total) | the harness opens and holds them                          |
| Sustained connections       | held for the whole soak (no drop)             | `livecore_realtime_connections` gauge (summed across instances)           |
| Reveal latency **p95**      | < 1500 ms                                     | `livecore_reveal_duration_seconds` histogram samples                      |
| Reveal latency **p99**      | < 3000 ms                                     | `livecore_reveal_duration_seconds` histogram samples                      |
| Fan-out delivery error rate | < 1%                                          | missed fan-out deliveries ÷ delivery attempts (+ `livecore_event_delivery_failures_total`, reported) |

Every parameter and threshold is read from an environment variable with the documented
default (`RealtimeHubLoadProfile`): `LIVECORE_REALTIME_LOAD_INSTANCES`,
`…_CONNECTIONS_PER_INSTANCE`, `…_REVEALS`, `…_P95_MS`, `…_P99_MS`,
`…_MAX_ERROR_RATE`, `…_TIMEOUT_SECONDS`, so the harness is repeatable and tunable
without a code change. The defaults above ARE the recorded baseline; an always-on test
fact pins them in code so this table and the harness cannot silently drift. The
baseline values are **conservative floors/ceilings validated on a modest 2 vCPU CI
runner** — per-instance capacity scales with the instance's CPU/memory sizing
(`docs/13`, CORE-DEP-007), so raise the target to measure a sized deployment.

**A regression below the baseline is detectable**: the harness asserts the sustained
connection count, the p95/p99 reveal latency and the delivery error rate against the
recorded thresholds, so a run that cannot sustain the target connections, whose latency
percentiles exceed the ceilings, or whose error rate exceeds the cap, **fails**.

**On-demand or scheduled, NOT a per-PR blocker.** The harness only does work when the
opt-in `LIVECORE_REALTIME_LOAD` flag is set **and** the shared PostgreSQL + Redis/Valkey
backplane are configured; a default `dotnet test` (and the per-PR `integration-postgres`
job, which never sets the flag) returns early, so the load/soak never slows a normal
build. The dedicated workflow `.github/workflows/realtime-load-baseline.yml` runs it on
a **weekly schedule and on demand** (`workflow_dispatch`, with optional inputs to raise
the target), stands up the Postgres + Valkey service containers, and uploads the measured
baseline summary line as a build artifact. The HA/scaling guidance in `docs/13` cites
this measured baseline.

## Tracing

Add trace propagation later when multiple services are deployed.

### Implementation (CORE-OBS-003)

The tracing hooks land ahead of that multi-service deployment so the seams exist when they are needed. They
are implemented with OpenTelemetry tracing over the vendor-neutral `System.Diagnostics` activity API. A single
owner, `LiveCoreActivitySource` (`apps/api/Observability/`), defines one `ActivitySource` named `LiveCore`
carrying the spans for the **key request and realtime flows**, and the existing seams produce spans on it:

| Flow                 | Span (operation name)             | Kind     | Produced by                                                  |
| -------------------- | --------------------------------- | -------- | ----------------------------------------------------------- |
| HTTP request         | `http.server.request`             | Server   | `RequestTracingMiddleware` (the whole request pipeline)     |
| Reveal command       | `livecore.reveal`                 | Internal | the reveal endpoint (`RevealEndpoints`, the reveal/hide path) |
| Session-event publish| `livecore.session_event.publish`  | Producer | `SessionEventPublisher` (append + deliver)                  |

The request span is opened at the top of the application pipeline, so it **wraps** authentication,
authorization and the endpoint — every span produced deeper nests under it. A reveal therefore produces one
trace shaped `request → reveal → publish` (each durable reveal/hide event a `publish` child of the reveal
span), which a collector reconstructs into the request's span tree.

The spans are exported with the OpenTelemetry SDK + host integration already present for the metrics
(`OpenTelemetry.Extensions.Hosting`). One new dependency is added to `apps/api`:
`OpenTelemetry.Exporter.OpenTelemetryProtocol` — the **OTLP** trace exporter, OpenTelemetry's vendor-neutral
export protocol that every major collector ingests (the OpenTelemetry Collector, Jaeger, Tempo, vendor
backends). It is wired **only when a collector endpoint is configured** (`Tracing:Otlp:Endpoint`); with nothing
configured the source is still registered with the `TracerProvider` (so spans are produced and any in-process
listener observes them) but shipped nowhere, so an unconfigured host never reaches a non-existent collector —
the same fail-closed/inert posture as the storage adapter, the realtime backplane and OIDC. The collector
endpoint is read from configuration only; none lives in source.

Like the metrics, every span carries only **low-cardinality, non-sensitive** attributes (threat T7 in
`docs/07_SECURITY_THREAT_MODEL.md`): the request span is tagged with the HTTP method, the route **template**
(never the concrete path, so no resource id becomes an attribute) and the status code; the reveal span with a
coarse `operation` name; the publish span with the stable session-event **type** name. No access token, tenant
identifier, participant id, asset coordinate or resource content is ever attached to a span. The frequently
polled, context-free infrastructure endpoints (`/health/*`, `/metrics`) are not traced. The background
**worker** is not yet instrumented for tracing (it owns no request/reveal flow); extending tracing to its jobs
is a follow-up.

### Auto-instrumentation and request/trace correlation (CORE-OBS-005)

CORE-OBS-003 subscribed only the hand-rolled `LiveCore` source, so a request trace held just the three
hand-rolled spans and the work that actually fails — a DB query, an outbound HTTP call, the framework's own
request handling — was **invisible**, and nothing returned the trace id to the caller, so a consumer could not
correlate a failed call with the server work behind it. CORE-OBS-005 closes both gaps.

**Auto-instrumentation.** Three OpenTelemetry-maintained instrumentations are added to the **same**
`TracerProvider` the OTLP exporter already drains (one new package each in `apps/api`:
`OpenTelemetry.Instrumentation.AspNetCore`, `.Http` and the prerelease `.EntityFrameworkCore` — justified in
`apps/api/LiveCore.Api.csproj` exactly as the Prometheus exporter's prerelease pin is, CORE-CMP-002):

| Subsystem            | Span kind | Produced by                                                           |
| -------------------- | --------- | --------------------------------------------------------------------- |
| ASP.NET Core request | Server    | `AddAspNetCoreInstrumentation` (the framework request span)           |
| Outbound HTTP        | Client    | `AddHttpClientInstrumentation` (every `System.Net.Http` call)         |
| EF Core database     | Client    | `AddEntityFrameworkCoreInstrumentation` (every database command)      |

Because they parent to `Activity.Current`, a DB query and an outbound HTTP call nest as **child spans under the
request span automatically**, so a trace now shows the request → {db, http, reveal → publish} tree a collector
reconstructs. They add **no** new exporter or endpoint — the OTLP exporter is still attached only when
`Tracing:Otlp:Endpoint` is configured. The same threat-T7 posture holds: the ASP.NET Core span is **filtered**
to skip the `/health` and `/metrics` infrastructure paths (the same paths the hand-rolled middleware skips); the
EF Core span never captures the SQL text or its parameters (`SetDbStatementForText` defaults off); and no span
carries a token, tenant identifier or content.

**Inbound propagation.** The ASP.NET Core hosting layer adopts an inbound **W3C `traceparent`** (the default
`DistributedContextPropagator`), so a request **continues the caller's trace** rather than starting a fresh one
— the precondition for end-to-end correlation across services.

**Returning the id to the caller.** `CorrelationHeaderMiddleware` writes two response headers on **every**
response (an authenticated JSON body, a Problem Details error, a fail-closed `401`/`403`, a `500` alike), from
an `OnStarting` callback so they survive the `Response.Clear()` an error path performs:

| Header          | Value                                                                                     |
| --------------- | ----------------------------------------------------------------------------------------- |
| `X-Request-Id`  | the per-request correlation id (a well-formed inbound `X-Request-Id`, else the trace id)   |
| `traceparent`   | the request span's full W3C trace context (`00-<trace-id>-<span-id>-<flags>`)              |

The correlation id is resolved **once** per request (`RequestCorrelation`, cached on `HttpContext.Items`) and
is the **same value** `RequestLogContextMiddleware` stamps as `request_id` on every log line, so the id a caller
reads off a failed response finds the matching server log lines, and the `traceparent` finds the matching
server trace. An inbound `X-Request-Id` is caller-controlled, so it is honored only when it is short and made of
a log-safe character set (ASCII letters/digits and `-`/`.`/`_`); anything else falls back to the trusted trace
id, so a correlation token can never forge a log line or smuggle content (threat T7). Both headers are
non-sensitive identifiers and the CORS policy **exposes** them so a browser/PWA SDK can read them (CORE-DX-005;
`docs/08_API_CONTRACTS.md`).

## Health checks

Required:

```text
/health/live
/health/ready
```

`/health/live` is **shallow**: it confirms the process is up and serving HTTP and
runs **no** dependency checks, so a failing dependency never restarts an otherwise
live process. `/health/ready` runs the checks tagged `ready` and gates whether the
host takes traffic.

Readiness checks the database **and the other critical dependencies** for actual
reachability (CORE-OBS-009). Beyond the database connectivity check and the
startup config-presence gate (CORE-OPS-005, which only records whether a dependency
is *configured*), readiness makes a **live, short-bounded probe** of each
**configured** critical dependency each time it is evaluated:

| Probe                | What it checks                                                        |
| -------------------- | -------------------------------------------------------------------- |
| `oidc-discovery`     | a GET of the OIDC provider's `.well-known/openid-configuration` answers |
| `object-storage`     | the S3-compatible backend answers (an account-level call round-trips) |
| `realtime-backplane` | the Redis/Valkey backplane answers a `PING`                          |

So a host with a healthy database but a **dead** critical dependency reports
not-ready (`503`) and leaves the rotation instead of taking traffic it cannot
serve. A dependency that is **not configured** is not probed (the in-process
single-instance backplane and the fail-closed unconfigured storage have nothing
live to reach), so with none configured the gate is inert. Each probe is bounded
by a short, configurable timeout (`HealthChecks:Readiness:ProbeTimeout`, default
`2s`; CORE-RES-005) and **fails closed** — a probe that errors or times out is
counted as not-ready, never as a false Ready. The unauthenticated readiness
response stays **status-only**, so which dependency is unreachable never leaks
(threat T7).

Readiness also gates on the database **schema being up to date** (CORE-OBS-010).
The schema ships as checked-in EF Core migrations applied by a separate,
run-to-completion deployment step **before** the API rolls out — the host never
migrates implicitly (CORE-OPS-001). So that a host whose migrate step was
skipped/failed, or that was rolled forward **ahead of its schema**, does not
advertise Ready and then `500` on the first domain query, the `database-schema`
readiness check asks EF Core for the migrations the build expects that the
database lacks (`GetPendingMigrationsAsync`): if any are missing the host reports
not-ready (`503`) and **leaves the rotation** until the schema is brought current,
at which point readiness flips back to ready. This is distinct from the
reachability probes above — connectivity proves the database *answers*, this
proves it carries the schema this build was compiled against. The read is bounded
both by the configured database command timeout (CORE-RES-004) and the same short
readiness wall-clock bound (`HealthChecks:Readiness:ProbeTimeout`; CORE-RES-005),
and **fails closed** — a read that errors or times out is counted not-ready. The
response stays **status-only**, so which migrations are missing never leaks
(threat T7), and `/health/live` stays shallow and unaffected (a stale schema never
restarts a live process).
