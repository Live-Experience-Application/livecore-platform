// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;

namespace LiveCore.Api.Observability;

/// <summary>
/// The single owner of the Core platform's distributed-tracing instrumentation (CORE-OBS-003). It defines one
/// <see cref="ActivitySource"/> (<see cref="SourceName"/>) and exposes a small <c>Start*</c> method per
/// instrumented flow, so the key request and realtime flows docs/15_OBSERVABILITY.md calls out — the HTTP
/// request pipeline and the reveal/publish path — produce spans through ONE source. Concentrating the source
/// here keeps the tracing CONTRACT in one place (the source name an exporter subscribes to and the span names
/// it emits) rather than scattering ad-hoc <see cref="ActivitySource"/> instances across modules, exactly as
/// <see cref="LiveCoreMetrics"/> is the single owner of the operational meters.
///
/// The instruments use <see cref="System.Diagnostics"/> activities — the vendor-neutral .NET tracing API the
/// OpenTelemetry SDK collects from — so STARTING a span is decoupled from how it is exported. The API host
/// subscribes this source to an OpenTelemetry <c>TracerProvider</c> and (when a collector endpoint is
/// configured) exports the spans over OTLP (<see cref="TracingServiceCollectionExtensions"/>); a host that
/// wires no listener simply gets a no-op (<see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <see langword="null"/> when nothing is listening), so an uninstrumented host pays nothing and a
/// caller must null-check the returned activity — never an error.
///
/// THREAT T7 (log/telemetry leaks, docs/07_SECURITY_THREAT_MODEL.md). Every span carries only LOW-CARDINALITY,
/// non-sensitive attributes — an HTTP method, a route TEMPLATE (never a concrete path, so no resource id is a
/// tag), a status code, a coarse operation name and a stable event-type name. No access token, tenant
/// identifier, participant id, asset coordinate or resource content is ever attached to a span, mirroring the
/// status-only metrics surface.
/// </summary>
public sealed class LiveCoreActivitySource : IDisposable
{
    /// <summary>
    /// The name of the single <see cref="ActivitySource"/> that owns every Core span. An exporter (or a test
    /// listener) subscribes BY THIS NAME (<see cref="TracingServiceCollectionExtensions"/>), and the tests
    /// assert the documented spans are produced under it.
    /// </summary>
    public const string SourceName = "LiveCore";

    private readonly ActivitySource _source;

    /// <summary>
    /// Creates the source. Constructed once per host as a singleton; the source is disposed with the
    /// container.
    /// </summary>
    public LiveCoreActivitySource() => _source = new ActivitySource(SourceName, "1.0.0");

    /// <summary>
    /// Starts the SERVER span for one HTTP request (the docs/15_OBSERVABILITY.md "Key request flow"). The
    /// caller (<see cref="RequestTracingMiddleware"/>) names the span and attaches the method, route TEMPLATE
    /// and status once routing has run. Returns <see langword="null"/> when nothing is listening.
    /// </summary>
    public Activity? StartHttpServerRequest() => _source.StartActivity("http.server.request", ActivityKind.Server);

    /// <summary>
    /// Starts the span for one reveal-family command (the docs/15_OBSERVABILITY.md realtime "reveal/publish
    /// path"), tagged with the coarse <paramref name="operation"/> name (e.g. <c>reveal</c>) so the reveal and
    /// any sibling visibility command can be told apart. The reveal's session-event publishes nest under this
    /// span as child spans. Returns <see langword="null"/> when nothing is listening.
    /// </summary>
    public Activity? StartReveal(string operation)
    {
        var activity = _source.StartActivity("livecore.reveal", ActivityKind.Internal);
        activity?.SetTag("operation", operation);
        return activity;
    }

    /// <summary>
    /// Starts the PRODUCER span for publishing one session event to its recipients (the realtime "publish"
    /// half of the reveal/publish path), tagged with the stable <paramref name="eventType"/> vocabulary name
    /// (e.g. <c>ContentRevealed</c>) — never the event payload (threat T7). Returns <see langword="null"/>
    /// when nothing is listening.
    /// </summary>
    public Activity? StartSessionEventPublish(string eventType)
    {
        var activity = _source.StartActivity("livecore.session_event.publish", ActivityKind.Producer);
        activity?.SetTag("livecore.event.type", eventType);
        return activity;
    }

    /// <inheritdoc />
    public void Dispose() => _source.Dispose();
}
