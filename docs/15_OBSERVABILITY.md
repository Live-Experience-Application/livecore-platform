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
| `request_id`      | `RequestLogContextMiddleware`   | the per-request correlation id (`HttpContext.TraceIdentifier`) |
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
meter named `LiveCore` carrying all eight instruments, and the existing seams record onto it: a request
middleware (request duration + error rate), the realtime hub (connections), the reveal endpoint (reveal
latency), the session-event publisher (event-delivery failures), a transparent `IAssetStorage` decorator
(asset upload/download failures), an EF Core command interceptor (database failures) and the worker's
background jobs (job failures, tagged by a coarse `job` name — one per loop: `asset-cleanup`,
`recap-generation`, `export-processing` and `store-notification-reconciliation`).

The API host exposes a **Prometheus scrape endpoint** at `GET /metrics` (the OpenTelemetry Prometheus
exporter). It is registered unconditionally — like the health endpoints, it needs no database or identity
provider. The instruments and their exported Prometheus series:

| Signal                       | Instrument                          | Kind          | Exported series (prefix)              |
| ---------------------------- | ----------------------------------- | ------------- | ------------------------------------- |
| API request duration         | `livecore.api.request.duration`     | histogram (s) | `livecore_api_request_duration_seconds` |
| API error rate               | `livecore.api.request.errors`       | counter       | `livecore_api_request_errors_total`   |
| Realtime connections         | `livecore.realtime.connections`     | up/down gauge | `livecore_realtime_connections`       |
| Reveal command latency       | `livecore.reveal.duration`          | histogram (s) | `livecore_reveal_duration_seconds`    |
| Event delivery failures      | `livecore.event.delivery.failures`  | counter       | `livecore_event_delivery_failures_total` |
| Asset upload/download failures| `livecore.asset.failures`          | counter       | `livecore_asset_failures_total`       |
| Database query failures      | `livecore.database.failures`        | counter       | `livecore_database_failures_total`    |
| Background job failures      | `livecore.job.failures`             | counter       | `livecore_job_failures_total`         |

Dimensions are kept **low-cardinality and non-sensitive** (threat T7): the request duration is tagged with
the HTTP method, the route **template** (never the concrete path, so no resource id becomes a label) and the
status code; the others carry only a coarse `operation`/`job` name. The error counter increments only for
server errors (5xx); the fail-closed 401/403/404 the authorization model returns by design are client-side
statuses and are not counted as errors.

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
CVE-scanned before publish (`docs/13_SELF_HOSTING_REQUIREMENTS.md`, CORE-DEP-003). The pin is revisited when a
stable `OpenTelemetry.Exporter.Prometheus.AspNetCore` is published.

The background **worker** records job failures onto the same `LiveCore` meter from all four of its job loops
(asset cleanup, recap generation, export processing and the billing-gated store-notification reconciliation),
and it now exposes its OWN Prometheus scrape endpoint at `GET /metrics` (CORE-DR-003), wired exactly as the
API host wires it (`AddLiveCorePrometheusMetrics` + `MapLiveCoreMetricsEndpoint`) — so the
`livecore_job_failures_total` counters each loop records on failure are actually scrapeable, not recorded onto
an unobserved meter. The worker binds the surface on a configurable listen URL (`Worker:Metrics:Url`, default
port 9464) and, like the API's `/metrics`, it is unauthenticated by convention and restricted at the network
edge, carrying only low-cardinality aggregates (threat T7). An OTLP push exporter remains a configuration
follow-up — the instruments are export-agnostic.

The worker also serves a per-loop `GET /health/live` endpoint (CORE-DR-003) backed by the same surface; see
`docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Worker liveness heartbeat").

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
and adding cross-service context propagation are follow-ups.

## Health checks

Required:

```text
/health/live
/health/ready
```

Readiness checks database and critical dependencies.
