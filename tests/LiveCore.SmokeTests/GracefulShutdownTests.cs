// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Hosting;
using LiveCore.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveCore.SmokeTests;

/// <summary>
/// Graceful-shutdown drain tests (CORE-DEP-002). On a rolling restart both hosts must DRAIN in-flight work
/// within a TUNED window rather than abruptly cutting it. These pin that:
/// <list type="bullet">
///   <item>both real hosts (the API <see cref="Program"/> and the worker <see cref="WorkerHostFactory"/>) apply
///   the tuned, configurable <see cref="GracefulShutdownOptions.DefaultShutdownTimeout"/> to
///   <see cref="HostOptions.ShutdownTimeout"/> instead of leaving it at the implicit framework default;</item>
///   <item>the window is read from configuration (<c>Hosting:ShutdownTimeout</c>), so a deployment tunes it in
///   lockstep with its orchestration grace period;</item>
///   <item>an in-flight HTTP request that completes inside the window is DRAINED, not abruptly cut, when the
///   host begins shutdown — the required behavior "a rolling restart does not abruptly cut an in-flight
///   request".</item>
/// </list>
/// </summary>
public class GracefulShutdownTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GracefulShutdownTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void ApiHost_applies_the_tuned_shutdown_drain_window()
    {
        // The API host (Program.cs) wires AddLiveCoreGracefulShutdown, so HostOptions.ShutdownTimeout is the
        // tuned default rather than the implicit framework default. Resolving IOptions<HostOptions> runs every
        // Configure<HostOptions> callback, including ours.
        var hostOptions = _factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(GracefulShutdownOptions.DefaultShutdownTimeout, hostOptions.ShutdownTimeout);
    }

    [Fact]
    public void WorkerHost_applies_the_tuned_shutdown_drain_window()
    {
        // The worker host applies the SAME drain window as the API, so BOTH hosts drain within a tuned window.
        using var host = WorkerHostFactory.Create([]);

        var hostOptions = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(GracefulShutdownOptions.DefaultShutdownTimeout, hostOptions.ShutdownTimeout);
    }

    [Fact]
    public void WorkerHost_honors_a_configured_shutdown_drain_window()
    {
        // A deployment tunes the window through configuration (Hosting:ShutdownTimeout) to match its own
        // orchestration grace period; the configured value is applied verbatim.
        using var host = WorkerHostFactory.Create(["--Hosting:ShutdownTimeout=00:00:07"]);

        var hostOptions = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(7), hostOptions.ShutdownTimeout);
    }

    [Fact]
    public async Task In_flight_request_is_drained_not_cut_when_the_host_stops()
    {
        // Build a minimal real-Kestrel host wired with the SAME AddLiveCoreGracefulShutdown the API and worker
        // use, bound to a dynamic loopback port. A slow endpoint signals it has started, then completes after a
        // short delay — modelling an in-flight request when a rolling restart begins.
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLiveCoreGracefulShutdown(builder.Configuration);

        await using var app = builder.Build();
        app.MapGet("/slow", async () =>
        {
            requestStarted.TrySetResult();
            // Comfortably shorter than the tuned drain window, so the host has time to drain it.
            await Task.Delay(TimeSpan.FromSeconds(1));
            return Results.Text("drained");
        });

        await app.StartAsync(overall.Token);

        var baseAddress = ResolveBoundAddress(app.Services);
        using var client = new HttpClient();
        var inflight = client.GetAsync($"{baseAddress}/slow", overall.Token);

        // Begin shutdown only AFTER the request is actually being handled — the in-flight request must drain.
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), overall.Token);
        var stopping = app.StopAsync(overall.Token);

        using var response = await inflight;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("drained", await response.Content.ReadAsStringAsync(overall.Token));

        await stopping;
    }

    private static string ResolveBoundAddress(IServiceProvider services)
    {
        var server = services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault();

        Assert.False(string.IsNullOrEmpty(address), "The host did not report a bound address.");
        return address!;
    }
}
