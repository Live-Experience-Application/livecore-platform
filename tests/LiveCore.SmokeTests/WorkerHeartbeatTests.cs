// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Observability;
using LiveCore.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.SmokeTests;

/// <summary>
/// Worker liveness-heartbeat tests (CORE-OPS-005, per-loop under CORE-DR-003). The worker runs up to four job
/// loops; a sweep that HANGS would leave the process alive yet doing no work, invisible to a process-liveness
/// check. The heartbeat is each loop's liveness signal: every loop writes a timestamp to its OWN file each
/// tick, so a wedged loop stops refreshing its file and that file goes stale — even while the other loops keep
/// beating theirs — the signal orchestration uses to detect (and restart) a stalled worker.
///
/// These tests pin: a per-loop heartbeat writes a fresh, round-trippable timestamp to its own file; the
/// cleanup loop emits an initial heartbeat when it starts (so the signal responds); the host wires the
/// per-loop heartbeat registry (and tracks exactly the loops that are configured); and the configured base
/// file path is honored. The aggregate single-loop-stall detection is pinned in
/// <see cref="WorkerJobLivenessTests"/>.
/// </summary>
public class WorkerHeartbeatTests
{
    private const string _databaseArgument =
        "--ConnectionStrings:Database=Host=localhost;Database=livecore;Username=livecore;Password=ignored";

    [Fact]
    public async Task Heartbeat_writes_a_fresh_round_trippable_timestamp_to_its_own_file()
    {
        var basePath = TempHeartbeatPath();
        var options = new WorkerHeartbeatOptions(basePath, WorkerHeartbeatOptions.DefaultStaleAfter);
        var heartbeat = new WorkerHeartbeat(
            WorkerJobNames.AssetCleanup, options, TimeProvider.System, NullLogger<WorkerHeartbeat>.Instance);
        try
        {
            var before = DateTimeOffset.UtcNow;
            await heartbeat.BeatAsync(CancellationToken.None);

            // The file is the per-loop file derived from the base path, not the bare base path.
            Assert.Equal(options.FilePathForJob(WorkerJobNames.AssetCleanup), heartbeat.FilePath);
            Assert.True(File.Exists(heartbeat.FilePath));
            Assert.True(WorkerHeartbeat.TryReadLastBeat(heartbeat.FilePath, out var lastBeat));
            Assert.InRange(lastBeat, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        }
        finally
        {
            TryDelete(heartbeat.FilePath);
        }
    }

    [Fact]
    public void TryReadLastBeat_is_false_when_the_heartbeat_file_is_absent()
    {
        Assert.False(WorkerHeartbeat.TryReadLastBeat(TempHeartbeatPath(), out _));
    }

    [Fact]
    public async Task Cleanup_loop_writes_an_initial_heartbeat_when_it_starts()
    {
        var basePath = TempHeartbeatPath();
        var heartbeatOptions = new WorkerHeartbeatOptions(basePath, WorkerHeartbeatOptions.DefaultStaleAfter);
        var heartbeats = new WorkerJobHeartbeats(
            heartbeatOptions, TimeProvider.System, NullLoggerFactory.Instance, [WorkerJobNames.AssetCleanup]);
        var jobFile = heartbeats.ForJob(WorkerJobNames.AssetCleanup).FilePath;
        try
        {
            // An empty provider with no cleanup service registered: the sweep fails fast and is swallowed by
            // the resilient loop, but the INITIAL heartbeat is written before the first sweep, so the liveness
            // signal still responds. A long sweep interval keeps the loop parked after that first beat.
            await using var provider = new ServiceCollection().BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var options = new AssetCleanupOptions(TimeSpan.FromHours(24), TimeSpan.FromHours(1), batchSize: 100);
            var service = new AssetCleanupBackgroundService(
                scopeFactory, options, heartbeats, new LiveCoreMetrics(),
                NullLogger<AssetCleanupBackgroundService>.Instance);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ((IHostedService)service).StartAsync(timeout.Token);

            await WaitForFileAsync(jobFile, TimeSpan.FromSeconds(10));

            await ((IHostedService)service).StopAsync(CancellationToken.None);

            Assert.True(WorkerHeartbeat.TryReadLastBeat(jobFile, out _));
        }
        finally
        {
            TryDelete(jobFile);
        }
    }

    [Fact]
    public void WorkerHost_tracks_the_configured_loops_when_a_database_is_configured()
    {
        using var host = WorkerHostFactory.Create([_databaseArgument]);

        Assert.NotNull(host.Services.GetService<WorkerJobHeartbeats>());
        Assert.NotNull(host.Services.GetService<WorkerHeartbeatOptions>());

        // With a database (and no billing opt-in) the three unconditional loops are active and expected to beat;
        // the billing-gated store-reconciliation loop is not.
        var heartbeats = host.Services.GetRequiredService<WorkerJobHeartbeats>();
        Assert.Contains(WorkerJobNames.AssetCleanup, heartbeats.JobNames);
        Assert.Contains(WorkerJobNames.RecapGeneration, heartbeats.JobNames);
        Assert.Contains(WorkerJobNames.ExportProcessing, heartbeats.JobNames);
        Assert.DoesNotContain(WorkerJobNames.StoreNotificationReconciliation, heartbeats.JobNames);
    }

    [Fact]
    public void WorkerHost_tracks_no_loops_without_a_database()
    {
        // No database -> no loops -> nothing to stall. The registry is still registered (so /health/live always
        // responds), but it tracks no loops, so liveness is vacuously healthy.
        using var host = WorkerHostFactory.Create([]);

        var heartbeats = host.Services.GetRequiredService<WorkerJobHeartbeats>();

        Assert.Empty(heartbeats.JobNames);
    }

    [Fact]
    public void WorkerHost_honors_the_configured_heartbeat_file_path()
    {
        var path = TempHeartbeatPath();
        using var host = WorkerHostFactory.Create([_databaseArgument, $"--Worker:Heartbeat:FilePath={path}"]);

        var options = host.Services.GetRequiredService<WorkerHeartbeatOptions>();

        Assert.Equal(path, options.FilePath);
        // Each loop's own file derives from the configured base path.
        Assert.Equal($"{path}.{WorkerJobNames.AssetCleanup}", options.FilePathForJob(WorkerJobNames.AssetCleanup));
    }

    private static string TempHeartbeatPath()
        => Path.Combine(Path.GetTempPath(), $"livecore-worker-heartbeat-test-{Guid.NewGuid():N}");

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.True(File.Exists(path), $"Heartbeat file was not written within {timeout}.");
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
