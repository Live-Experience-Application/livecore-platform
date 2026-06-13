using LiveCore.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync(timeout.Token);
        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);

        await host.StopAsync(timeout.Token);
        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public void WorkerHost_registers_the_asset_cleanup_job_when_a_database_is_configured()
    {
        // CORE-AST-006: with a database configured the worker schedules the asset cleanup job. The
        // connection string is supplied through configuration only (no connection is opened by building the
        // host), exactly as the API host reads it.
        using var host = WorkerHostFactory.Create(
            ["--ConnectionStrings:Database=Host=localhost;Database=livecore;Username=livecore;Password=ignored"])
            .Build();

        var hostedServices = host.Services.GetServices<IHostedService>();

        Assert.Contains(hostedServices, service => service is AssetCleanupBackgroundService);
    }

    [Fact]
    public void WorkerHost_registers_no_cleanup_job_without_a_database()
    {
        // Persistence-gated, fail-safe: with no database configured the worker still starts but schedules no
        // cleanup loop (mirroring how the API host runs without persistence).
        using var host = WorkerHostFactory.Create([]).Build();

        var hostedServices = host.Services.GetServices<IHostedService>();

        Assert.DoesNotContain(hostedServices, service => service is AssetCleanupBackgroundService);
    }

    [Fact]
    public void WorkerHost_registers_the_recap_generation_job_when_a_database_is_configured()
    {
        // CORE-JOB-001: with a database configured the worker schedules the recap generation job alongside the
        // cleanup job. The connection string is supplied through configuration only (no connection is opened by
        // building the host), exactly as the API host reads it.
        using var host = WorkerHostFactory.Create(
            ["--ConnectionStrings:Database=Host=localhost;Database=livecore;Username=livecore;Password=ignored"])
            .Build();

        var hostedServices = host.Services.GetServices<IHostedService>();

        Assert.Contains(hostedServices, service => service is RecapGenerationBackgroundService);
    }

    [Fact]
    public void WorkerHost_registers_no_recap_generation_job_without_a_database()
    {
        // Persistence-gated, fail-safe: with no database configured the worker schedules no recap generation
        // loop (mirroring the cleanup job's gating).
        using var host = WorkerHostFactory.Create([]).Build();

        var hostedServices = host.Services.GetServices<IHostedService>();

        Assert.DoesNotContain(hostedServices, service => service is RecapGenerationBackgroundService);
    }

    [Fact]
    public void WorkerHost_registers_the_export_processing_job_when_a_database_is_configured()
    {
        // CORE-JOB-002: with a database configured the worker schedules the export processing job alongside the
        // other jobs. The connection string is supplied through configuration only (no connection is opened by
        // building the host), exactly as the API host reads it.
        using var host = WorkerHostFactory.Create(
            ["--ConnectionStrings:Database=Host=localhost;Database=livecore;Username=livecore;Password=ignored"])
            .Build();

        var hostedServices = host.Services.GetServices<IHostedService>();

        Assert.Contains(hostedServices, service => service is ExportProcessingBackgroundService);
    }

    [Fact]
    public void WorkerHost_registers_no_export_processing_job_without_a_database()
    {
        // Persistence-gated, fail-safe: with no database configured the worker schedules no export processing
        // loop (mirroring the other jobs' gating).
        using var host = WorkerHostFactory.Create([]).Build();

        var hostedServices = host.Services.GetServices<IHostedService>();

        Assert.DoesNotContain(hostedServices, service => service is ExportProcessingBackgroundService);
    }
}
