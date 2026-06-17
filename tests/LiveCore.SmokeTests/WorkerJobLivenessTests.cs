// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using LiveCore.Worker;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.SmokeTests;

/// <summary>
/// Per-loop worker liveness tests (CORE-DR-003). Before this story the four loops shared one heartbeat file,
/// so a single healthy loop kept the file fresh and MASKED three hung ones — a monitoring blind spot on the
/// host doing irreversible work. These tests pin the fix: each loop beats its own file and the aggregate
/// liveness verdict (<see cref="WorkerJobHeartbeats.CheckLiveness"/>, surfaced by
/// <see cref="WorkerHeartbeatLivenessHealthCheck"/> on <c>/health/live</c>) is healthy only when EVERY active
/// loop is beating, so a single stalled loop is detectable rather than hidden.
///
/// Time is controlled deterministically with the real clock: a "fresh" loop is beaten now, and a "stalled"
/// loop's file is written directly with a timestamp older than the staleness threshold — no fake clock or
/// sleeping required.
/// </summary>
public class WorkerJobLivenessTests
{
    private static readonly string[] _allLoops =
    [
        WorkerJobNames.AssetCleanup,
        WorkerJobNames.RecapGeneration,
        WorkerJobNames.ExportProcessing,
        WorkerJobNames.StoreNotificationReconciliation,
    ];

    [Fact]
    public async Task A_single_stalled_loop_is_detected_while_the_other_loops_stay_live()
    {
        var basePath = TempHeartbeatPath();
        var options = new WorkerHeartbeatOptions(basePath, TimeSpan.FromHours(2));
        var heartbeats = new WorkerJobHeartbeats(
            options, TimeProvider.System, NullLoggerFactory.Instance, _allLoops);
        try
        {
            // Three loops beat now (fresh); the fourth's heartbeat is 3 hours old — past the 2-hour threshold —
            // exactly the "one loop hung while the others keep working" case the shared file used to mask.
            await heartbeats.ForJob(WorkerJobNames.AssetCleanup).BeatAsync(CancellationToken.None);
            await heartbeats.ForJob(WorkerJobNames.RecapGeneration).BeatAsync(CancellationToken.None);
            await heartbeats.ForJob(WorkerJobNames.ExportProcessing).BeatAsync(CancellationToken.None);
            WriteStaleBeat(options.FilePathForJob(WorkerJobNames.StoreNotificationReconciliation), TimeSpan.FromHours(3));

            var report = heartbeats.CheckLiveness();

            Assert.False(report.IsLive);
            Assert.Equal([WorkerJobNames.StoreNotificationReconciliation], report.StaleJobs);
            Assert.Contains(WorkerJobNames.AssetCleanup, report.LiveJobs);
            Assert.Contains(WorkerJobNames.RecapGeneration, report.LiveJobs);
            Assert.Contains(WorkerJobNames.ExportProcessing, report.LiveJobs);

            // The liveness health check that backs /health/live reports not-live, so orchestration restarts the
            // wedged worker.
            var result = await new WorkerHeartbeatLivenessHealthCheck(heartbeats)
                .CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }
        finally
        {
            DeleteAll(options);
        }
    }

    [Fact]
    public async Task All_loops_beating_is_live()
    {
        var basePath = TempHeartbeatPath();
        var options = new WorkerHeartbeatOptions(basePath, TimeSpan.FromHours(2));
        var heartbeats = new WorkerJobHeartbeats(
            options, TimeProvider.System, NullLoggerFactory.Instance, _allLoops);
        try
        {
            foreach (var loop in _allLoops)
            {
                await heartbeats.ForJob(loop).BeatAsync(CancellationToken.None);
            }

            var report = heartbeats.CheckLiveness();

            Assert.True(report.IsLive);
            Assert.Empty(report.StaleJobs);

            var result = await new WorkerHeartbeatLivenessHealthCheck(heartbeats)
                .CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            DeleteAll(options);
        }
    }

    [Fact]
    public async Task A_loop_that_never_beat_is_stale_fail_closed()
    {
        var basePath = TempHeartbeatPath();
        var options = new WorkerHeartbeatOptions(basePath, TimeSpan.FromHours(2));
        var heartbeats = new WorkerJobHeartbeats(
            options, TimeProvider.System, NullLoggerFactory.Instance, _allLoops);
        try
        {
            // Only one loop ever beats; the other three have no heartbeat file at all. A missing heartbeat must
            // read as stalled (fail-closed), never as healthy-by-default.
            await heartbeats.ForJob(WorkerJobNames.AssetCleanup).BeatAsync(CancellationToken.None);

            var report = heartbeats.CheckLiveness();

            Assert.False(report.IsLive);
            Assert.Contains(WorkerJobNames.RecapGeneration, report.StaleJobs);
            Assert.Contains(WorkerJobNames.ExportProcessing, report.StaleJobs);
            Assert.Contains(WorkerJobNames.StoreNotificationReconciliation, report.StaleJobs);
        }
        finally
        {
            DeleteAll(options);
        }
    }

    [Fact]
    public async Task No_active_loops_is_vacuously_live()
    {
        var options = new WorkerHeartbeatOptions(TempHeartbeatPath(), TimeSpan.FromHours(2));
        var heartbeats = new WorkerJobHeartbeats(
            options, TimeProvider.System, NullLoggerFactory.Instance, []);

        var report = heartbeats.CheckLiveness();

        Assert.True(report.IsLive);
        Assert.Empty(report.StaleJobs);

        var result = await new WorkerHeartbeatLivenessHealthCheck(heartbeats)
            .CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private static void WriteStaleBeat(string path, TimeSpan age)
    {
        var stale = (DateTimeOffset.UtcNow - age).ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(path, stale);
    }

    private static string TempHeartbeatPath()
        => Path.Combine(Path.GetTempPath(), $"livecore-worker-liveness-test-{Guid.NewGuid():N}");

    private static void DeleteAll(WorkerHeartbeatOptions options)
    {
        foreach (var loop in _allLoops)
        {
            TryDelete(options.FilePathForJob(loop));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp file; ignore.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp file; ignore.
        }
    }
}
