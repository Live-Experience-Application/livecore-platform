// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LiveCore.Api.Observability;

/// <summary>
/// Dependency-injection wiring for the Core distributed-tracing surface (CORE-OBS-003, extended by
/// CORE-OBS-005). It binds the single <see cref="LiveCoreActivitySource"/> AND the framework / outbound-HTTP /
/// EF Core auto-instrumentations to an OpenTelemetry <c>TracerProvider</c> and, when a collector endpoint is
/// configured, exports the produced spans over OTLP, realizing the docs/15_OBSERVABILITY.md tracing surface
/// (which docs/15 explicitly defers to "later, when multiple services are deployed" — this is that work, added
/// ahead of the multi-service deployment so the seams exist).
///
/// WHY OPENTELEMETRY OTLP (the CORE-OBS-003 justified new dependency). docs/15 mandates that key request and
/// realtime flows be exportable to a configured collector; <see cref="LiveCoreActivitySource"/> produces the
/// spans on the vendor-neutral <see cref="System.Diagnostics"/> API, but a process still needs an SDK to
/// sample/batch them and a wire protocol to ship them. OpenTelemetry is the CNCF-standard, vendor-neutral
/// tracing SDK for .NET and OTLP is its vendor-neutral export protocol (every major collector — the
/// OpenTelemetry Collector, Jaeger, Tempo, vendor backends — ingests OTLP); reimplementing span batching and
/// the OTLP wire format by hand would duplicate a correctness-sensitive subsystem. The SDK + host integration
/// is already present (<c>OpenTelemetry.Extensions.Hosting</c>, added for the CORE-OBS-001 metrics);
/// CORE-OBS-003 added <c>OpenTelemetry.Exporter.OpenTelemetryProtocol</c> (the OTLP trace exporter).
///
/// WHY THE AUTO-INSTRUMENTATIONS (the CORE-OBS-005 justified new dependencies). Before CORE-OBS-005 the only
/// subscribed source was the hand-rolled <see cref="LiveCoreActivitySource.SourceName"/>, so the request trace
/// held just the three hand-rolled spans and the work that actually fails — a DB query, an outbound HTTP call,
/// the framework's own request handling — was invisible, so a consumer could not correlate a failed call with
/// the server work behind it. <c>AddAspNetCoreInstrumentation</c>,
/// <c>AddHttpClientInstrumentation</c> and <c>AddEntityFrameworkCoreInstrumentation</c> are the
/// OpenTelemetry-maintained instrumentations that turn each of those subsystems into spans on the SAME
/// <c>TracerProvider</c>, so a DB query and an outbound HTTP call nest as CHILD spans under the request span
/// automatically (they parent to <c>Activity.Current</c>). They add NO new exporter or endpoint — they feed the
/// same OTLP pipeline.
///
/// FAIL-CLOSED / INERT BY DEFAULT (the same posture as the storage adapter, the realtime backplane and OIDC).
/// The exporter is attached ONLY when a collector endpoint is configured (<c>Tracing:Otlp:Endpoint</c>); with
/// nothing configured the source is still registered with the tracer provider (so spans are produced and any
/// in-process listener observes them) but no exporter runs, so an unconfigured host never tries to reach a
/// non-existent collector and never logs export errors. No telemetry backend, endpoint or credential is read
/// from source; the endpoint is supplied at runtime via configuration only (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// THREAT T7 (low-cardinality, no content). Every span carries only low-cardinality, non-sensitive attributes:
/// the ASP.NET Core instrumentation is FILTERED to skip the frequently-polled, context-free <c>/health</c> and
/// <c>/metrics</c> infrastructure paths (the same paths the hand-rolled request middleware skips); the EF Core
/// instrumentation never captures the SQL text or its parameters (<c>SetDbStatementForText</c> defaults off);
/// and no instrumentation attaches an access token, tenant identifier, participant id or resource content.
/// </summary>
public static class TracingServiceCollectionExtensions
{
    /// <summary>The configuration key naming the OTLP collector endpoint a deployment exports traces to.</summary>
    public const string OtlpEndpointConfigurationKey = "Tracing:Otlp:Endpoint";

    /// <summary>The OpenTelemetry resource <c>service.name</c> the API host's exported traces are tagged with.</summary>
    private const string _serviceName = "livecore-api";

    /// <summary>
    /// The frequently-polled, context-free infrastructure path prefixes the ASP.NET Core instrumentation skips,
    /// mirroring <see cref="RequestTracingMiddleware"/> and <see cref="RequestLogContextMiddleware"/> so the
    /// auto-instrumented request span follows the same "do not trace infrastructure noise" posture
    /// docs/15_OBSERVABILITY.md documents.
    /// </summary>
    private static readonly string[] _untracedInfrastructurePathPrefixes = ["/health", "/metrics"];

    /// <summary>
    /// Registers the shared <see cref="LiveCoreActivitySource"/> as a singleton. Used by any host that
    /// produces Core spans. Idempotent (<see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    public static IServiceCollection AddLiveCoreTracing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<LiveCoreActivitySource>();
        return services;
    }

    /// <summary>
    /// Registers the shared <see cref="LiveCoreActivitySource"/> AND the OpenTelemetry tracing pipeline that
    /// exports its spans: a <c>TracerProvider</c> subscribed to the <see cref="LiveCoreActivitySource.SourceName"/>
    /// source, with an OTLP exporter attached only when <see cref="OtlpEndpointConfigurationKey"/> is
    /// configured. Used by the API host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration (read for the optional OTLP endpoint).</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">The service collection or configuration is null.</exception>
    public static IServiceCollection AddLiveCoreOpenTelemetryTracing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddLiveCoreTracing();

        var otlpEndpoint = configuration[OtlpEndpointConfigurationKey];

        services
            .AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(_serviceName))
                    .AddSource(LiveCoreActivitySource.SourceName)
                    // Framework request auto-instrumentation (CORE-OBS-005). Produces the SERVER span every DB /
                    // outbound-HTTP / hand-rolled span nests under, FILTERED to skip the /health and /metrics
                    // infrastructure paths so it follows the same "do not trace infrastructure noise" posture as
                    // the hand-rolled request middleware. It also honors an inbound W3C traceparent (the ASP.NET
                    // Core hosting layer adopts the caller's trace context), so a request continues the caller's
                    // trace rather than starting a fresh one.
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = static context => !IsUntracedInfrastructurePath(context.Request.Path))
                    // Outbound HTTP auto-instrumentation (CORE-OBS-005): every System.Net.Http call (the OIDC
                    // back-channel, a store verification call, a future downstream) becomes a CLIENT span nested
                    // under the request span, and PROPAGATES the trace context downstream via traceparent.
                    .AddHttpClientInstrumentation()
                    // EF Core database auto-instrumentation (CORE-OBS-005): every database command becomes a
                    // CLIENT span nested under the request span. The SQL text and its parameters are never
                    // captured (SetDbStatementForText defaults off), so a DB span carries no content (threat T7).
                    .AddEntityFrameworkCoreInstrumentation();

                // Attach the OTLP exporter only when a collector endpoint is configured; otherwise the spans
                // are produced but not shipped anywhere (no noisy connection attempts to a non-existent
                // collector). The endpoint comes from configuration only (threat T7).
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return services;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one of the frequently-polled, context-free infrastructure paths
    /// (<c>/health/*</c>, <c>/metrics</c>) the ASP.NET Core instrumentation does not trace.
    /// </summary>
    private static bool IsUntracedInfrastructurePath(PathString path)
        => _untracedInfrastructurePathPrefixes.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
