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
background job (job failures).

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

The background **worker** records job failures onto the same `LiveCore` meter, but as a non-HTTP host it does
not yet expose its own scrape surface; surfacing the worker's metrics over a scrape/OTLP endpoint is a
follow-up (the API host owns the `/metrics` surface today). Likewise, an OTLP push exporter is a configuration
follow-up — the instruments are export-agnostic.

## Tracing

Add trace propagation later when multiple services are deployed.

## Health checks

Required:

```text
/health/live
/health/ready
```

Readiness checks database and critical dependencies.
