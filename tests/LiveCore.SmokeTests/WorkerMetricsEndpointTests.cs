// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Observability;
using LiveCore.Worker;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.SmokeTests;

/// <summary>
/// Worker metrics-surface tests (CORE-DR-003). The worker records the docs/15_OBSERVABILITY.md "background job
/// failures" signal (<c>livecore_job_failures_total</c>) from each loop, but used to expose no way to scrape
/// it — a monitoring blind spot on the host doing irreversible work. The worker now serves the same Prometheus
/// <c>/metrics</c> endpoint the API host serves, plus a per-loop <c>/health/live</c> endpoint.
///
/// These tests pin: the metrics endpoint RESPONDS and REFLECTS a forced job failure (the required behavior),
/// and the liveness endpoint responds. The surface is bound to a dynamic loopback port so the test never
/// conflicts with another test or anything else on a fixed port.
/// </summary>
public class WorkerMetricsEndpointTests
{
    [Fact]
    public async Task Worker_metrics_endpoint_responds_and_reflects_a_forced_job_failure()
    {
        // No database is needed to exercise the metric -> exporter -> scrape path: the meter and Prometheus
        // exporter are wired unconditionally (exactly like the API's /metrics). Bind to a dynamic loopback port.
        using var host = WorkerHostFactory.Create(["--Worker:Metrics:Url=http://127.0.0.1:0"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.StartAsync(timeout.Token);
        try
        {
            // Force a job failure exactly as a failing sweep does: record the "background job failures" signal
            // onto the shared LiveCoreMetrics — the same meter the Prometheus exporter observes.
            host.Services.GetRequiredService<LiveCoreMetrics>()
                .RecordBackgroundJobFailure(WorkerJobNames.AssetCleanup);

            var baseAddress = ResolveBoundAddress(host.Services);
            using var client = new HttpClient();

            var metrics = await client.GetStringAsync($"{baseAddress}/metrics", timeout.Token);

            // The forced failure surfaces as the documented Prometheus series, attributed to the coarse loop
            // name (and nothing more — no tenant id, token or resource content; threat T7).
            Assert.Contains("livecore_job_failures_total", metrics);
            Assert.Contains(WorkerJobNames.AssetCleanup, metrics);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_metrics_endpoint_reflects_job_success_duration_and_backlog()
    {
        // The CORE-OBS-007 worker SLIs (success count, duration histogram, backlog/queue depth) record onto the
        // SAME shared LiveCoreMetrics each loop records its success path onto; drive them exactly as a completed
        // sweep does and assert all three documented series surface on the worker's own /metrics, attributed to
        // the coarse loop name (and nothing more — no tenant id, token or content; threat T7).
        using var host = WorkerHostFactory.Create(["--Worker:Metrics:Url=http://127.0.0.1:0"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.StartAsync(timeout.Token);
        try
        {
            var metrics = host.Services.GetRequiredService<LiveCoreMetrics>();
            metrics.RecordBackgroundJobSuccess(WorkerJobNames.ExportProcessing);
            metrics.RecordBackgroundJobDuration(WorkerJobNames.ExportProcessing, 0.42);
            metrics.RecordBackgroundJobBacklog(WorkerJobNames.ExportProcessing, 5);

            var baseAddress = ResolveBoundAddress(host.Services);
            using var client = new HttpClient();

            var scraped = await client.GetStringAsync($"{baseAddress}/metrics", timeout.Token);

            Assert.Contains("livecore_job_successes_total", scraped);
            Assert.Contains("livecore_job_duration_seconds", scraped);
            Assert.Contains("livecore_job_backlog", scraped);
            Assert.Contains(WorkerJobNames.ExportProcessing, scraped);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_liveness_endpoint_responds_and_is_healthy_with_no_loops_to_stall()
    {
        // With no database there are no loops to stall, so /health/live is vacuously healthy and responds 200
        // with a status-only body (no loop names or details leak; threat T7).
        using var host = WorkerHostFactory.Create(["--Worker:Metrics:Url=http://127.0.0.1:0"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.StartAsync(timeout.Token);
        try
        {
            var baseAddress = ResolveBoundAddress(host.Services);
            using var client = new HttpClient();

            using var response = await client.GetAsync($"{baseAddress}/health/live", timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Contains("Healthy", body);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_readiness_is_not_ready_in_production_with_no_database_while_liveness_stays_healthy()
    {
        // CORE-OBS-013: the readiness signal is DISTINCT from per-loop liveness and the worker is NOT vacuously
        // healthy. In Production with no database the worker can do no work: /health/live stays 200 (a process
        // with nothing to stall is genuinely alive) but /health/ready is 503 NOT-READY, so the worker leaves the
        // ready rotation instead of advertising a readiness it does not have.
        using var host = WorkerHostFactory.Create(
            ["--environment", "Production", "--Worker:Metrics:Url=http://127.0.0.1:0"]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.StartAsync(timeout.Token);
        try
        {
            var baseAddress = ResolveBoundAddress(host.Services);
            using var client = new HttpClient();

            using var live = await client.GetAsync($"{baseAddress}/health/live", timeout.Token);
            using var ready = await client.GetAsync($"{baseAddress}/health/ready", timeout.Token);
            var readyBody = await ready.Content.ReadAsStringAsync(timeout.Token);

            // Liveness is vacuously healthy (nothing to stall); readiness is not (no work to do).
            Assert.True(live.IsSuccessStatusCode);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Contains("Unhealthy", readyBody);
            // Status-only: no check name, key or reason leaks to the unauthenticated response (threat T7).
            Assert.DoesNotContain("ConnectionStrings", readyBody, StringComparison.Ordinal);
            Assert.DoesNotContain("persistence", readyBody, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_readiness_is_ready_in_production_with_a_database_and_active_loops()
    {
        // The other side of the gate: a correctly configured production worker (a database is configured, so the
        // loops are scheduled) reports READY. The connection string is supplied through configuration only — no
        // connection is opened by building/starting the host, exactly as the API host reads it.
        using var host = WorkerHostFactory.Create(
        [
            "--environment", "Production",
            "--Worker:Metrics:Url=http://127.0.0.1:0",
            "--ConnectionStrings:Database=Host=localhost;Database=livecore;Username=livecore;Password=ignored",
        ]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.StartAsync(timeout.Token);
        try
        {
            var baseAddress = ResolveBoundAddress(host.Services);
            using var client = new HttpClient();

            using var ready = await client.GetAsync($"{baseAddress}/health/ready", timeout.Token);
            var readyBody = await ready.Content.ReadAsStringAsync(timeout.Token);

            Assert.True(ready.IsSuccessStatusCode);
            Assert.Contains("Healthy", readyBody);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static string ResolveBoundAddress(IServiceProvider services)
    {
        var server = services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault();

        Assert.False(string.IsNullOrEmpty(address), "The worker metrics listener did not report a bound address.");
        return address!;
    }
}
