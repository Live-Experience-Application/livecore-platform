namespace LiveCore.Api.Observability;

/// <summary>
/// Endpoint wiring for the Prometheus scrape surface (CORE-OBS-001). It owns the <c>/metrics</c> route in one
/// place so the host's <c>Program.cs</c> maps it by intent (<see cref="MapLiveCoreMetricsEndpoint"/>) rather
/// than calling the exporter extension directly, mirroring how <c>MapLiveCoreHealthEndpoints</c> owns the
/// health routes.
///
/// The endpoint is unauthenticated by convention — a Prometheus server scrapes it from inside the deployment
/// network, exactly as orchestration probes the unauthenticated <c>/health/*</c> endpoints — and a
/// deployment restricts it at the reverse-proxy/network edge (docs/13_SELF_HOSTING_REQUIREMENTS.md). It
/// exposes only the low-cardinality aggregate series <see cref="LiveCoreMetrics"/> emits, never content or
/// identifiers (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public static class MetricsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the Prometheus scrape endpoint at <c>/metrics</c> (the exporter's default path), serving the
    /// OpenTelemetry-collected <see cref="LiveCoreMetrics"/> series in the Prometheus exposition format.
    /// Requires <see cref="MetricsServiceCollectionExtensions.AddLiveCorePrometheusMetrics"/> to have wired
    /// the exporter.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same endpoint route builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">The endpoint route builder is null.</exception>
    public static IEndpointRouteBuilder MapLiveCoreMetricsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPrometheusScrapingEndpoint();
        return endpoints;
    }
}
