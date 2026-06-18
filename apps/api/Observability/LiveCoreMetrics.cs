// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LiveCore.Api.Observability;

/// <summary>
/// The single owner of the Core platform's operational metrics (CORE-OBS-001, extended with the
/// service-level indicators of CORE-OBS-007). It defines, on one <see cref="Meter"/> (<see cref="MeterName"/>),
/// the eight signals docs/15_OBSERVABILITY.md originally requires Core to track — API request duration, API
/// error rate, realtime connections, reveal command latency, event delivery failures, asset upload/download
/// failures, database query failures and background job failures — PLUS the five key service-level indicators
/// CORE-OBS-007 adds so an operator can alert on an auth-failure spike, a rate-limit storm or a worker falling
/// behind: the authentication/authorization-failure (401/403) rate, the rate-limit (429) rejection count, and
/// per-loop worker job success count, duration histogram and backlog/queue depth. It exposes a small
/// <c>Record*</c> method per signal that the existing seams call (the request pipeline, the realtime hub, the
/// reveal command, the event publisher, the asset storage adapter, the persistence layer and the worker's
/// background loops). Concentrating the instrument definitions here keeps the metric CONTRACT in one place —
/// the names, kinds and units a Prometheus/OTLP surface exposes — rather than scattering ad-hoc meters across
/// modules, exactly as the Visibility module is the single place visibility is decided
/// (docs/05_MODULE_CONTRACTS.md). The SLIs REUSE this same meter and the existing low-cardinality tag keys (no
/// new meter, no new dependency).
///
/// The instruments use <see cref="System.Diagnostics.Metrics"/> — the vendor-neutral .NET metrics API the
/// OpenTelemetry SDK collects from — so recording a measurement is decoupled from how it is exported. The
/// API host subscribes this meter to an OpenTelemetry <c>MeterProvider</c> and exposes it on a Prometheus
/// scrape endpoint (<see cref="MetricsServiceCollectionExtensions"/>); a host that wires no exporter (or
/// resolves this type before the provider starts) simply records to an unobserved meter — recording is a
/// cheap no-op with no listener, never an error.
///
/// THREAT T7 (log/telemetry leaks, docs/07_SECURITY_THREAT_MODEL.md). Every measurement carries only
/// LOW-CARDINALITY, non-sensitive dimensions — an HTTP method, a route TEMPLATE (never a concrete path, so no
/// resource id is ever a label), a status code, a coarse operation name and a job name. No tenant identifier,
/// participant id, token, asset coordinate or resource content is ever attached to a metric, so the scrape
/// surface cannot leak content even though it is reachable without authentication (mirroring the
/// status-only health endpoints).
/// </summary>
public sealed class LiveCoreMetrics : IDisposable
{
    /// <summary>
    /// The name of the single meter that owns every Core operational instrument. An exporter subscribes to
    /// the meter BY THIS NAME (<see cref="MetricsServiceCollectionExtensions"/>), and the integration test
    /// asserts the documented instruments surface on the scrape endpoint under it.
    /// </summary>
    public const string MeterName = "LiveCore";

    // Tag keys follow the OpenTelemetry HTTP semantic conventions where one applies, so the exported series
    // line up with standard dashboards. They are all low-cardinality (method, route TEMPLATE, status code,
    // coarse operation/job name); a concrete request path or any tenant/resource identifier is NEVER a tag
    // (threat T7).
    private const string _httpMethodTag = "http.request.method";
    private const string _httpRouteTag = "http.route";
    private const string _httpStatusCodeTag = "http.response.status_code";
    private const string _operationTag = "operation";
    private const string _jobTag = "job";

    private readonly Meter _meter;

    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _requestErrors;
    private readonly Counter<long> _authFailures;
    private readonly Counter<long> _rateLimitRejections;
    private readonly UpDownCounter<long> _realtimeConnections;
    private readonly Histogram<double> _revealDuration;
    private readonly Counter<long> _eventDeliveryFailures;
    private readonly Counter<long> _assetFailures;
    private readonly Counter<long> _databaseFailures;
    private readonly Counter<long> _jobFailures;
    private readonly Counter<long> _jobSuccesses;
    private readonly Histogram<double> _jobDuration;
    private readonly Gauge<long> _jobBacklog;

    /// <summary>
    /// Creates the meter and its thirteen instruments (the eight original signals plus the five CORE-OBS-007
    /// service-level indicators). Constructed once per host as a singleton; the meter is disposed with the
    /// container.
    /// </summary>
    public LiveCoreMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        // API request duration (docs/15). A histogram of server-side request handling time in seconds; its
        // count also yields total request throughput, and its status-code dimension is the denominator for
        // the error rate below.
        _requestDuration = _meter.CreateHistogram<double>(
            "livecore.api.request.duration",
            unit: "s",
            description: "Duration of an API request handled by the host, in seconds.");

        // API error rate (docs/15). A counter of requests that failed with a server (5xx) status; divided by
        // the request total it is the error rate. Client-side statuses (4xx) — including the fail-closed
        // 401/403/404 the authorization model returns by design — are deliberately NOT counted as errors.
        _requestErrors = _meter.CreateCounter<long>(
            "livecore.api.request.errors",
            unit: "{error}",
            description: "API requests that completed with a server-error (5xx) status.");

        // Authentication/authorization-failure rate (SLI, CORE-OBS-007). A counter of requests the
        // authorization model rejected fail-closed with a 401 (unauthenticated) or 403 (forbidden). These are
        // client-side statuses the error counter above deliberately does NOT count as server faults, so a spike
        // of credential/permission failures (a brute-force or token-replay attempt, a misconfigured client) was
        // previously invisible; this makes it alertable. The status-code dimension keeps 401 and 403 distinct.
        _authFailures = _meter.CreateCounter<long>(
            "livecore.api.auth.failures",
            unit: "{failure}",
            description: "API requests rejected with an authentication/authorization failure (401 or 403).");

        // Rate-limit rejection count (SLI, CORE-OBS-007). A counter of requests the rate limiter rejected with
        // 429 (CORE-SEC-001). Like the auth-failure counter it is a 4xx the error counter does not count, so a
        // rate-limit storm — an abusive client or a misconfigured caller hammering the limiter — is alertable
        // on its own series rather than hidden among normal 4xx traffic.
        _rateLimitRejections = _meter.CreateCounter<long>(
            "livecore.api.rate_limit.rejections",
            unit: "{rejection}",
            description: "API requests rejected by the rate limiter with a 429 (Too Many Requests) status.");

        // Realtime connections (docs/15). An up/down counter of currently-open authenticated realtime hub
        // connections: incremented when a connection is admitted to its server-managed groups and decremented
        // when it disconnects, so the exported value is the live connection gauge.
        _realtimeConnections = _meter.CreateUpDownCounter<long>(
            "livecore.realtime.connections",
            unit: "{connection}",
            description: "Currently-open authenticated realtime hub connections.");

        // Reveal command latency (docs/15). A histogram of how long the Visibility module's reveal command
        // takes, in seconds — the central host-driven state change of a live session.
        _revealDuration = _meter.CreateHistogram<double>(
            "livecore.reveal.duration",
            unit: "s",
            description: "Latency of the Visibility reveal command, in seconds.");

        // Event delivery failures (docs/15). A counter incremented when delivering a session event to its
        // computed recipients fails at the realtime transport (the durable event is already persisted, so a
        // delivery failure loses only the live push — the metric makes that visible).
        _eventDeliveryFailures = _meter.CreateCounter<long>(
            "livecore.event.delivery.failures",
            unit: "{failure}",
            description: "Realtime session-event deliveries that failed at the transport.");

        // Asset upload/download failures (docs/15). A counter, tagged by operation (upload/download),
        // incremented when a CONFIGURED object-storage backend fails to mint a signed URL. The fail-closed
        // "storage not configured" outcome is expected behavior, not a backend failure, and is not counted.
        _assetFailures = _meter.CreateCounter<long>(
            "livecore.asset.failures",
            unit: "{failure}",
            description: "Asset upload/download operations that failed against a configured storage backend.");

        // Database query failures (docs/15). A counter incremented when an executed database command fails,
        // observed through an EF Core command interceptor so every query path is covered uniformly.
        _databaseFailures = _meter.CreateCounter<long>(
            "livecore.database.failures",
            unit: "{failure}",
            description: "Database commands that failed during execution.");

        // Background job failures (docs/15). A counter, tagged by job name, incremented when a worker
        // background job run throws, so a job that keeps failing (rather than hanging — that is the liveness
        // heartbeat's signal) is observable.
        _jobFailures = _meter.CreateCounter<long>(
            "livecore.job.failures",
            unit: "{failure}",
            description: "Background job runs that failed with an unhandled error.");

        // Background job success count (SLI, CORE-OBS-007). A counter, tagged by the coarse job name, of sweep
        // runs that COMPLETED without throwing — the success counterpart to the failure counter above. Together
        // they give the per-loop success/failure ratio, so a loop that is silently failing every sweep (or that
        // has stopped succeeding) is visible rather than only the absolute failure rate.
        _jobSuccesses = _meter.CreateCounter<long>(
            "livecore.job.successes",
            unit: "{success}",
            description: "Background job runs that completed without an unhandled error.");

        // Background job duration (SLI, CORE-OBS-007). A histogram, tagged by the coarse job name, of how long
        // one sweep took, in seconds — so a loop whose sweeps are getting slower (approaching its interval) is
        // observable before it falls behind.
        _jobDuration = _meter.CreateHistogram<double>(
            "livecore.job.duration",
            unit: "s",
            description: "Duration of one background job sweep run, in seconds.");

        // Background job backlog/queue depth (SLI, CORE-OBS-007). A gauge, tagged by the coarse job name, set
        // after each sweep to the number of pending items that sweep observed (its examined count). A depth
        // that stays saturated at the batch size sweep after sweep is the "worker falling behind" signal an
        // operator alerts on — a queue could previously back up while every existing metric stayed green.
        _jobBacklog = _meter.CreateGauge<long>(
            "livecore.job.backlog",
            unit: "{item}",
            description: "Pending items a background job sweep observed (its queue/backlog depth).");
    }

    /// <summary>
    /// Records one completed API request: its handling <paramref name="durationSeconds"/> on the duration
    /// histogram (tagged with the method, the route TEMPLATE and the status code), and one increment of the
    /// matching service-level counter with the SAME tags — the error counter for a server error (5xx), the
    /// auth-failure counter for a fail-closed 401/403 (CORE-OBS-007) and the rate-limit counter for a 429
    /// (CORE-OBS-007). The three are mutually exclusive by status, so a request increments at most one. The
    /// caller passes the route TEMPLATE (e.g. <c>/api/v1/sessions/{sessionId}/reveal</c>), never the concrete
    /// path, so no resource id becomes a label, and no tenant/principal detail is ever attached (threat T7).
    /// </summary>
    public void RecordApiRequest(string method, string route, int statusCode, double durationSeconds)
    {
        var tags = new TagList
        {
            { _httpMethodTag, method },
            { _httpRouteTag, route },
            { _httpStatusCodeTag, statusCode },
        };

        _requestDuration.Record(durationSeconds, tags);

        // Classify the status into the matching SLI counter. Only genuine server faults (5xx) are errors; the
        // fail-closed 401/403 and the 429 are client-side statuses tracked on their own SLI series so an
        // operator can alert on an auth-failure spike or a rate-limit storm without conflating them with faults.
        if (statusCode >= 500)
        {
            _requestErrors.Add(1, tags);
        }
        else if (statusCode is 401 or 403)
        {
            _authFailures.Add(1, tags);
        }
        else if (statusCode == 429)
        {
            _rateLimitRejections.Add(1, tags);
        }
    }

    /// <summary>Increments the live realtime-connection gauge when a connection is admitted.</summary>
    public void RecordRealtimeConnectionOpened() => _realtimeConnections.Add(1);

    /// <summary>Decrements the live realtime-connection gauge when an admitted connection disconnects.</summary>
    public void RecordRealtimeConnectionClosed() => _realtimeConnections.Add(-1);

    /// <summary>
    /// Records the latency of one reveal-family command, in seconds, tagged by <paramref name="operation"/>
    /// (e.g. <c>reveal</c>) so the reveal and any sibling visibility command can be told apart on the
    /// histogram.
    /// </summary>
    public void RecordRevealCommand(double durationSeconds, string operation)
        => _revealDuration.Record(durationSeconds, new TagList { { _operationTag, operation } });

    /// <summary>Increments the realtime event-delivery failure counter.</summary>
    public void RecordEventDeliveryFailure() => _eventDeliveryFailures.Add(1);

    /// <summary>
    /// Increments the asset failure counter for a failed <paramref name="operation"/> (<c>upload</c> or
    /// <c>download</c>) against a configured storage backend.
    /// </summary>
    public void RecordAssetFailure(string operation)
        => _assetFailures.Add(1, new TagList { { _operationTag, operation } });

    /// <summary>Increments the database query-failure counter.</summary>
    public void RecordDatabaseFailure() => _databaseFailures.Add(1);

    /// <summary>
    /// Increments the background job-failure counter for the named <paramref name="job"/> (e.g.
    /// <c>asset-cleanup</c>).
    /// </summary>
    public void RecordBackgroundJobFailure(string job)
        => _jobFailures.Add(1, new TagList { { _jobTag, job } });

    /// <summary>
    /// Increments the background job-SUCCESS counter for the named <paramref name="job"/> — one sweep run that
    /// completed without throwing (SLI, CORE-OBS-007). The coarse job name is the only dimension; no tenant,
    /// principal or content is ever attached (threat T7).
    /// </summary>
    public void RecordBackgroundJobSuccess(string job)
        => _jobSuccesses.Add(1, new TagList { { _jobTag, job } });

    /// <summary>
    /// Records the <paramref name="durationSeconds"/> of one sweep run of the named <paramref name="job"/> on
    /// the job-duration histogram (SLI, CORE-OBS-007), tagged only by the coarse job name (threat T7).
    /// </summary>
    public void RecordBackgroundJobDuration(string job, double durationSeconds)
        => _jobDuration.Record(durationSeconds, new TagList { { _jobTag, job } });

    /// <summary>
    /// Sets the backlog/queue-depth gauge for the named <paramref name="job"/> to <paramref name="depth"/> —
    /// the number of pending items the latest sweep observed (SLI, CORE-OBS-007), tagged only by the coarse job
    /// name (threat T7). A depth that stays saturated sweep after sweep signals a loop falling behind.
    /// </summary>
    public void RecordBackgroundJobBacklog(string job, long depth)
        => _jobBacklog.Record(depth, new TagList { { _jobTag, job } });

    /// <summary>
    /// This instance's underlying <see cref="Meter"/>. Exposed to the test assemblies only (the meter name is
    /// process-global, so a test that listens by name alone would capture another concurrently-running host's
    /// measurements; a test scopes its <see cref="MeterListener"/> to THIS reference for a deterministic
    /// assertion). Not part of the public surface.
    /// </summary>
    internal Meter Meter => _meter;

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
