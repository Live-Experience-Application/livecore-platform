// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="DependencyReadinessHealthCheck"/> (CORE-OBS-009) — the readiness check that runs the
/// configured live dependency probes, bounded and fail-closed. Using fake probes (no real OIDC/storage/backplane)
/// they pin the behavior the acceptance criteria require: every probe healthy => Ready; ANY probe unreachable =>
/// not-ready (so a host with a dead critical dependency leaves the rotation); the failure description names the
/// unreachable dependency for the server-side log only; a hung probe is HARD-bounded by the short timeout and
/// counted as not-ready (fail-closed, CORE-RES-005); and an empty probe set is inert (Ready). Generic,
/// product-neutral vocabulary only (AGENTS.md).
/// </summary>
public class DependencyReadinessHealthCheckTests
{
    private static readonly DependencyReadinessOptions _fastTimeout =
        new(TimeSpan.FromMilliseconds(150));

    private static Task<HealthCheckResult> CheckAsync(
        DependencyReadinessOptions options, params IDependencyReadinessProbe[] probes)
        => new DependencyReadinessHealthCheck(probes, options)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task Reports_healthy_when_no_probe_is_configured()
    {
        var result = await CheckAsync(DependencyReadinessOptions.Default);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Reports_healthy_when_every_probe_is_reachable()
    {
        var result = await CheckAsync(
            DependencyReadinessOptions.Default,
            FakeProbe.Reachable("oidc-discovery"),
            FakeProbe.Reachable("object-storage"),
            FakeProbe.Reachable("realtime-backplane"));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Reports_unhealthy_when_any_probe_is_unreachable()
    {
        var result = await CheckAsync(
            DependencyReadinessOptions.Default,
            FakeProbe.Reachable("oidc-discovery"),
            FakeProbe.Unreachable("object-storage"),
            FakeProbe.Reachable("realtime-backplane"));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        // The server-side description names the unreachable dependency but never the reachable ones or any secret.
        Assert.Contains("object-storage", result.Description);
        Assert.DoesNotContain("oidc-discovery", result.Description);
    }

    [Fact]
    public async Task A_hung_probe_is_bounded_by_the_short_timeout_and_counted_not_ready()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await CheckAsync(
            _fastTimeout,
            FakeProbe.Reachable("oidc-discovery"),
            FakeProbe.Hangs("object-storage"));

        stopwatch.Stop();

        // Fail-closed: a probe that never returns is treated as unreachable, and the readiness response is bounded
        // by the short timeout rather than hanging (CORE-RES-005).
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("object-storage", result.Description);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"The readiness check should have been bounded by the short timeout, but it took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DependencyReadinessHealthCheck(null!, DependencyReadinessOptions.Default));
        Assert.Throws<ArgumentNullException>(
            () => new DependencyReadinessHealthCheck([], null!));
        await Task.CompletedTask;
    }

    private sealed class FakeProbe : IDependencyReadinessProbe
    {
        private readonly Func<CancellationToken, Task> _probe;

        private FakeProbe(string dependencyName, Func<CancellationToken, Task> probe)
        {
            DependencyName = dependencyName;
            _probe = probe;
        }

        public string DependencyName { get; }

        public Task ProbeAsync(CancellationToken cancellationToken) => _probe(cancellationToken);

        public static FakeProbe Reachable(string name) => new(name, _ => Task.CompletedTask);

        public static FakeProbe Unreachable(string name) =>
            new(name, _ => throw new InvalidOperationException($"{name} is unreachable"));

        public static FakeProbe Hangs(string name) =>
            new(name, token => Task.Delay(Timeout.InfiniteTimeSpan, token));
    }
}
