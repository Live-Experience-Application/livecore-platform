using LiveCore.Api.Assets;
using LiveCore.Api.Observability;
using LiveCore.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.SmokeTests;

/// <summary>
/// Worker liveness-heartbeat tests (CORE-OPS-005). The worker runs the asset cleanup loop; a sweep that HANGS
/// would leave the process alive yet doing no work, invisible to a process-liveness check. The heartbeat is
/// the loop's liveness signal: the loop writes a timestamp to a file each tick, so a wedged loop stops
/// refreshing it and the file goes stale — the signal orchestration uses to detect (and restart) a stalled
/// worker.
///
/// These tests pin: the heartbeat writes a fresh, round-trippable timestamp; the cleanup loop emits an initial
/// heartbeat when it starts (so the signal responds); the host wires the heartbeat alongside the cleanup job
/// (and not without a database, where there is no loop to stall); and the configured file path is honored.
/// </summary>
public class WorkerHeartbeatTests
{
    private const string _databaseArgument =
        "--ConnectionStrings:Database=Host=localhost;Database=livecore;Username=livecore;Password=ignored";

    [Fact]
    public async Task Heartbeat_writes_a_fresh_round_trippable_timestamp_to_the_configured_file()
    {
        var path = TempHeartbeatPath();
        try
        {
            var heartbeat = new WorkerHeartbeat(
                new WorkerHeartbeatOptions(path), TimeProvider.System, NullLogger<WorkerHeartbeat>.Instance);

            var before = DateTimeOffset.UtcNow;
            await heartbeat.BeatAsync(CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.True(WorkerHeartbeat.TryReadLastBeat(path, out var lastBeat));
            Assert.InRange(lastBeat, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        }
        finally
        {
            TryDelete(path);
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
        var path = TempHeartbeatPath();
        try
        {
            // An empty provider with no cleanup service registered: the sweep fails fast and is swallowed by
            // the resilient loop, but the INITIAL heartbeat is written before the first sweep, so the liveness
            // signal still responds. A long sweep interval keeps the loop parked after that first beat.
            await using var provider = new ServiceCollection().BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var options = new AssetCleanupOptions(TimeSpan.FromHours(24), TimeSpan.FromHours(1), batchSize: 100);
            var heartbeat = new WorkerHeartbeat(
                new WorkerHeartbeatOptions(path), TimeProvider.System, NullLogger<WorkerHeartbeat>.Instance);
            var service = new AssetCleanupBackgroundService(
                scopeFactory, options, heartbeat, new LiveCoreMetrics(),
                NullLogger<AssetCleanupBackgroundService>.Instance);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ((IHostedService)service).StartAsync(timeout.Token);

            await WaitForFileAsync(path, TimeSpan.FromSeconds(10));

            await ((IHostedService)service).StopAsync(CancellationToken.None);

            Assert.True(WorkerHeartbeat.TryReadLastBeat(path, out _));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void WorkerHost_registers_the_heartbeat_when_a_database_is_configured()
    {
        using var host = WorkerHostFactory.Create([_databaseArgument]).Build();

        Assert.NotNull(host.Services.GetService<WorkerHeartbeat>());
        Assert.NotNull(host.Services.GetService<WorkerHeartbeatOptions>());
    }

    [Fact]
    public void WorkerHost_registers_no_heartbeat_without_a_database()
    {
        // No database -> no cleanup loop -> nothing to stall, so no heartbeat is wired (mirrors the cleanup
        // job's persistence gating).
        using var host = WorkerHostFactory.Create([]).Build();

        Assert.Null(host.Services.GetService<WorkerHeartbeat>());
    }

    [Fact]
    public void WorkerHost_honors_the_configured_heartbeat_file_path()
    {
        var path = TempHeartbeatPath();
        using var host = WorkerHostFactory.Create([_databaseArgument, $"--Worker:Heartbeat:FilePath={path}"]).Build();

        var heartbeat = host.Services.GetRequiredService<WorkerHeartbeat>();

        Assert.Equal(path, heartbeat.FilePath);
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
