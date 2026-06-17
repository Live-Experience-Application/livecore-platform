// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LiveCore.Api.Observability;

/// <summary>
/// Dependency-injection wiring for the Core distributed-tracing surface (CORE-OBS-003). It binds the single
/// <see cref="LiveCoreActivitySource"/> to an OpenTelemetry <c>TracerProvider</c> and, when a collector
/// endpoint is configured, exports the produced spans over OTLP, realizing the docs/15_OBSERVABILITY.md
/// tracing surface (which docs/15 explicitly defers to "later, when multiple services are deployed" — this is
/// that work, added ahead of the multi-service deployment so the seams exist).
///
/// WHY OPENTELEMETRY OTLP (the justified new dependency). docs/15 mandates that key request and realtime flows
/// be exportable to a configured collector; <see cref="LiveCoreActivitySource"/> produces the spans on the
/// vendor-neutral <see cref="System.Diagnostics"/> API, but a process still needs an SDK to sample/batch them
/// and a wire protocol to ship them. OpenTelemetry is the CNCF-standard, vendor-neutral tracing SDK for .NET
/// and OTLP is its vendor-neutral export protocol (every major collector — the OpenTelemetry Collector,
/// Jaeger, Tempo, vendor backends — ingests OTLP); reimplementing span batching and the OTLP wire format by
/// hand would duplicate a correctness-sensitive subsystem. The SDK + host integration is already present
/// (<c>OpenTelemetry.Extensions.Hosting</c>, added for the CORE-OBS-001 metrics); this story adds ONE package
/// to <c>apps/api</c>: <c>OpenTelemetry.Exporter.OpenTelemetryProtocol</c> (the OTLP trace exporter).
///
/// FAIL-CLOSED / INERT BY DEFAULT (the same posture as the storage adapter, the realtime backplane and OIDC).
/// The exporter is attached ONLY when a collector endpoint is configured (<c>Tracing:Otlp:Endpoint</c>); with
/// nothing configured the source is still registered with the tracer provider (so spans are produced and any
/// in-process listener observes them) but no exporter runs, so an unconfigured host never tries to reach a
/// non-existent collector and never logs export errors. No telemetry backend, endpoint or credential is read
/// from source; the endpoint is supplied at runtime via configuration only (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md), and the spans carry only the low-cardinality, non-sensitive attributes
/// <see cref="LiveCoreActivitySource"/> attaches.
/// </summary>
public static class TracingServiceCollectionExtensions
{
    /// <summary>The configuration key naming the OTLP collector endpoint a deployment exports traces to.</summary>
    public const string OtlpEndpointConfigurationKey = "Tracing:Otlp:Endpoint";

    /// <summary>The OpenTelemetry resource <c>service.name</c> the API host's exported traces are tagged with.</summary>
    private const string _serviceName = "livecore-api";

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
                    .AddSource(LiveCoreActivitySource.SourceName);

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
}
