// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;

namespace LiveCore.Api.Observability;

/// <summary>
/// Dependency-injection and endpoint wiring for the Core operational metrics (CORE-OBS-001). It binds the
/// single <see cref="LiveCoreMetrics"/> instrument set to an OpenTelemetry <c>MeterProvider</c> and exposes
/// it on a Prometheus scrape endpoint, realizing the docs/15_OBSERVABILITY.md metrics surface.
///
/// WHY OPENTELEMETRY (the justified new dependency). docs/15 mandates eight operational metrics (the
/// CORE-OBS-007 service-level indicators later add five more on the SAME meter, no new dependency); none
/// existed (no meter, no scrape/OTLP surface). <see cref="LiveCoreMetrics"/> defines the instruments on the
/// vendor-neutral <see cref="System.Diagnostics.Metrics"/> API, but a process still needs an SDK to
/// aggregate those measurements and serve them in a scrapeable format. OpenTelemetry is the CNCF-standard,
/// vendor-neutral metrics SDK for .NET (docs/15 names OpenTelemetry); reimplementing metric aggregation and
/// the Prometheus exposition format by hand would duplicate a security- and correctness-sensitive subsystem.
/// Two packages are added to <c>apps/api</c>: <c>OpenTelemetry.Extensions.Hosting</c> (the SDK + its host
/// integration that starts the <c>MeterProvider</c>) and <c>OpenTelemetry.Exporter.Prometheus.AspNetCore</c>
/// (the <c>/metrics</c> scrape endpoint). No telemetry backend, endpoint or credential is read from source;
/// the scrape endpoint is pull-based and carries only the low-cardinality, non-sensitive series
/// <see cref="LiveCoreMetrics"/> emits (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// The scrape endpoint is UNAUTHENTICATED by convention (a Prometheus server scrapes it from inside the
/// deployment network, exactly as orchestration probes the unauthenticated health endpoints), so a
/// deployment restricts <c>/metrics</c> at the reverse-proxy/network edge — the same posture as
/// <c>/health/*</c> (docs/13_SELF_HOSTING_REQUIREMENTS.md). It never exposes content, only aggregates.
/// </summary>
public static class MetricsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared <see cref="LiveCoreMetrics"/> instrument set as a singleton. Used by any host
    /// that records Core metrics — including the background worker, which records job failures without
    /// exposing its own scrape endpoint. Idempotent (<see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    public static IServiceCollection AddLiveCoreMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<LiveCoreMetrics>();
        return services;
    }

    /// <summary>
    /// Registers the shared <see cref="LiveCoreMetrics"/> instruments AND the OpenTelemetry metrics pipeline
    /// that exports them over Prometheus: a <c>MeterProvider</c> subscribed to the <see cref="LiveCoreMetrics.MeterName"/>
    /// meter with the Prometheus exporter attached. The host then maps the scrape endpoint with
    /// <see cref="MetricsEndpointRouteBuilderExtensions.MapLiveCoreMetricsEndpoint"/>. Used by the API host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    public static IServiceCollection AddLiveCorePrometheusMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLiveCoreMetrics();

        services
            .AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(LiveCoreMetrics.MeterName)
                // Collect on every scrape so a series reflects the latest measurement (no stale cache); the
                // surface is pull-based and small, so per-scrape collection is cheap.
                .AddPrometheusExporter(options => options.ScrapeResponseCacheDurationMilliseconds = 0));

        return services;
    }
}
