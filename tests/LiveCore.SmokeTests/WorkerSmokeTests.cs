using LiveCore.Worker;

namespace LiveCore.SmokeTests;

/// <summary>
/// Smoke tests for the background worker host skeleton (CORE-FND-001).
/// </summary>
public class WorkerSmokeTests
{
    [Fact]
    public async Task WorkerHost_builds_starts_and_stops()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var host = WorkerHostFactory.Create([]).Build();

        await host.StartAsync(timeout.Token);
        await host.StopAsync(timeout.Token);
    }
}
