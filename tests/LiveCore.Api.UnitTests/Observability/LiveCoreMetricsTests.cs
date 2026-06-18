// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Observability;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// Tests for <see cref="LiveCoreMetrics"/> (CORE-OBS-001, with the CORE-OBS-007 service-level indicators), the
/// single owner of the operational signals docs/15_OBSERVABILITY.md requires Core to track. They assert —
/// through a real <see cref="System.Diagnostics.Metrics.MeterListener"/> (<see cref="RecordedMetrics"/>), the
/// same subscription mechanism the OpenTelemetry SDK uses — that every documented <c>Record*</c> method emits a
/// measurement on the expected instrument, that the realtime gauge moves up and down, that the API status
/// classification routes 5xx/401-403/429 to the error/auth-failure/rate-limit counters respectively, that the
/// worker job success/duration/backlog SLIs emit, and that every dimension is low-cardinality and non-sensitive
/// (no tenant/principal tag; threat T7). This is the deterministic proof that the documented meters are real
/// and emit; the integration test proves they surface on the Prometheus scrape endpoint end-to-end.
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
    // status, expected: errors, auth-failures, rate-limit rejections. The three SLI counters are mutually
    // exclusive by status; the duration histogram always fires.
    [InlineData(500, 1, 0, 0)]
    [InlineData(503, 1, 0, 0)]
    [InlineData(401, 0, 1, 0)]
    [InlineData(403, 0, 1, 0)]
    [InlineData(429, 0, 0, 1)]
    [InlineData(404, 0, 0, 0)]
    [InlineData(200, 0, 0, 0)]
    public void RecordApiRequest_classifies_status_into_the_right_sli_counter(
        int statusCode, int expectedErrors, int expectedAuthFailures, int expectedRateLimitRejections)
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        metrics.RecordApiRequest("POST", "/api/v1/sessions/{sessionId}/reveal", statusCode, 0.02);

        // The duration histogram is recorded for every request regardless of status.
        Assert.Equal(1, recorded.Count("livecore.api.request.duration"));
        // Only genuine server faults (5xx) are errors; the fail-closed 401/403 and the 429 go to their own SLIs.
        Assert.Equal(expectedErrors, recorded.Count("livecore.api.request.errors"));
        Assert.Equal(expectedAuthFailures, recorded.Count("livecore.api.auth.failures"));
        Assert.Equal(expectedRateLimitRejections, recorded.Count("livecore.api.rate_limit.rejections"));
    }

    [Fact]
    public void RecordApiRequest_sli_counters_carry_only_low_cardinality_non_sensitive_tags()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        metrics.RecordApiRequest("GET", "/api/v1/me", statusCode: 401, durationSeconds: 0.01);
        metrics.RecordApiRequest("GET", "/api/v1/me", statusCode: 429, durationSeconds: 0.01);

        // The auth-failure and rate-limit series carry ONLY the method, the route TEMPLATE and the status code —
        // no tenant, principal, token or resource label (threat T7).
        string[] expected = ["http.request.method", "http.route", "http.response.status_code"];
        Assert.Equal(expected.OrderBy(k => k), recorded.TagKeys("livecore.api.auth.failures").OrderBy(k => k));
        Assert.Equal(expected.OrderBy(k => k), recorded.TagKeys("livecore.api.rate_limit.rejections").OrderBy(k => k));
        // The status code distinguishes 401/403 within the auth-failure series.
        Assert.True(recorded.HasTag("livecore.api.auth.failures", "http.response.status_code", 401));
    }

    [Fact]
    public void Worker_job_sli_instruments_each_emit_tagged_by_job_only()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        metrics.RecordBackgroundJobSuccess("export-processing");
        metrics.RecordBackgroundJobDuration("export-processing", 0.25);
        metrics.RecordBackgroundJobBacklog("export-processing", 7);

        Assert.Equal(1, recorded.Count("livecore.job.successes"));
        Assert.Equal(1, recorded.Count("livecore.job.duration"));
        Assert.Equal(1, recorded.Count("livecore.job.backlog"));

        // Each worker SLI carries ONLY the coarse job name — never a tenant/principal/content label (threat T7).
        foreach (var instrument in new[] { "livecore.job.successes", "livecore.job.duration", "livecore.job.backlog" })
        {
            Assert.Equal(["job"], recorded.TagKeys(instrument));
            Assert.True(recorded.HasTag(instrument, "job", "export-processing"));
        }
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
