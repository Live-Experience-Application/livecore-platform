using LiveCore.Api.Observability;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration tests for the operational metrics surface (CORE-OBS-001) — the story's required test: "the
/// metrics endpoint responds and emits the documented meters". They boot the real API host and assert that
/// <c>GET /metrics</c> responds in the Prometheus exposition format and that all eight signals
/// docs/15_OBSERVABILITY.md requires (request duration, error rate, realtime connections, reveal latency,
/// event-delivery failures, asset failures, database failures and background-job failures) surface on it,
/// end-to-end through the real OpenTelemetry pipeline.
///
/// The signals are driven through the SAME <see cref="LiveCoreMetrics"/> instruments the production seams
/// record onto (resolved from the running host), plus a real HTTP request that exercises the request-duration
/// middleware — so the scrape surface is proven against the genuine instruments and exporter, not a mock. The
/// host needs no database or identity provider: the metrics surface is registered unconditionally, exactly
/// like the health endpoints.
/// </summary>
public sealed class MetricsEndpointTests
{
    private static readonly string[] _documentedMetricNames =
    [
        "livecore_api_request_duration",
        "livecore_api_request_errors",
        "livecore_realtime_connections",
        "livecore_reveal_duration",
        "livecore_event_delivery_failures",
        "livecore_asset_failures",
        "livecore_database_failures",
        "livecore_job_failures",
    ];

    [Fact]
    public async Task Metrics_endpoint_responds_and_emits_every_documented_meter()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        // A real request flows through the request-metrics middleware, producing the request-duration signal.
        using var health = await client.GetAsync("/health/ready");

        // Drive each documented signal through the real instruments the production seams record onto.
        var metrics = factory.Services.GetRequiredService<LiveCoreMetrics>();
        metrics.RecordApiRequest("POST", "/api/v1/sessions/{sessionId}/reveal", statusCode: 500, durationSeconds: 0.02);
        metrics.RecordRealtimeConnectionOpened();
        metrics.RecordRevealCommand(0.03, operation: "reveal");
        metrics.RecordEventDeliveryFailure();
        metrics.RecordAssetFailure("upload");
        metrics.RecordDatabaseFailure();
        metrics.RecordBackgroundJobFailure("asset-cleanup");

        using var response = await client.GetAsync("/metrics");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        foreach (var metricName in _documentedMetricNames)
        {
            Assert.Contains(metricName, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Metrics_endpoint_is_reachable_without_authentication()
    {
        // The scrape endpoint is unauthenticated by convention (a Prometheus server scrapes it from inside the
        // deployment network, like the health endpoints), so an anonymous request is not challenged with 401.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/metrics");

        Assert.True(response.IsSuccessStatusCode);
    }
}
