using LiveCore.Api.Observability;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// Tests for <see cref="LiveCoreMetrics"/> (CORE-OBS-001), the single owner of the eight operational signals
/// docs/15_OBSERVABILITY.md requires Core to track. They assert — through a real
/// <see cref="System.Diagnostics.Metrics.MeterListener"/> (<see cref="RecordedMetrics"/>), the same
/// subscription mechanism the OpenTelemetry SDK uses — that every documented <c>Record*</c> method emits a
/// measurement on the expected instrument, that the realtime gauge moves up and down, that the API error
/// counter fires only for server (5xx) statuses, and that the operation/job dimensions are attached. This is
/// the deterministic proof that all eight documented meters are real and emit; the integration test proves
/// they surface on the Prometheus scrape endpoint end-to-end.
///
/// The class shares the serialized "LiveCore metrics" collection so its listener never captures another
/// metric test's measurements.
/// </summary>
[Collection(LiveCoreMetricsTestCollection.Name)]
public sealed class LiveCoreMetricsTests
{
    [Fact]
    public void RecordApiRequest_emits_duration_for_every_request()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        metrics.RecordApiRequest("GET", "/api/v1/me", statusCode: 200, durationSeconds: 0.01);

        Assert.Equal(1, recorded.Count("livecore.api.request.duration"));
        Assert.True(recorded.HasTag("livecore.api.request.duration", "http.route", "/api/v1/me"));
        // A 200 is not an error.
        Assert.Equal(0, recorded.Count("livecore.api.request.errors"));
    }

    [Theory]
    [InlineData(500, 1)]
    [InlineData(503, 1)]
    [InlineData(404, 0)]
    [InlineData(403, 0)]
    [InlineData(200, 0)]
    public void RecordApiRequest_counts_only_server_errors(int statusCode, int expectedErrors)
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        metrics.RecordApiRequest("POST", "/api/v1/sessions/{sessionId}/reveal", statusCode, 0.02);

        // The duration histogram is recorded for every request regardless of status.
        Assert.Equal(1, recorded.Count("livecore.api.request.duration"));
        // The fail-closed client statuses (4xx) are not faults; only 5xx increments the error counter.
        Assert.Equal(expectedErrors, recorded.Count("livecore.api.request.errors"));
    }

    [Fact]
    public void Realtime_connection_gauge_moves_up_on_open_and_down_on_close()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        metrics.RecordRealtimeConnectionOpened();
        metrics.RecordRealtimeConnectionClosed();

        Assert.Equal(2, recorded.Count("livecore.realtime.connections"));
    }

    [Fact]
    public void RecordRevealCommand_emits_latency_tagged_by_operation()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        metrics.RecordRevealCommand(0.05, operation: "reveal");

        Assert.Equal(1, recorded.Count("livecore.reveal.duration"));
        Assert.True(recorded.HasTag("livecore.reveal.duration", "operation", "reveal"));
    }

    [Fact]
    public void Failure_counters_each_emit()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        metrics.RecordEventDeliveryFailure();
        metrics.RecordAssetFailure("download");
        metrics.RecordDatabaseFailure();
        metrics.RecordBackgroundJobFailure("asset-cleanup");

        Assert.Equal(1, recorded.Count("livecore.event.delivery.failures"));
        Assert.Equal(1, recorded.Count("livecore.asset.failures"));
        Assert.True(recorded.HasTag("livecore.asset.failures", "operation", "download"));
        Assert.Equal(1, recorded.Count("livecore.database.failures"));
        Assert.Equal(1, recorded.Count("livecore.job.failures"));
        Assert.True(recorded.HasTag("livecore.job.failures", "job", "asset-cleanup"));
    }
}
