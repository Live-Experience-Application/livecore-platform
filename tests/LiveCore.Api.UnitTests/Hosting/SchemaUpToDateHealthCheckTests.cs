// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="SchemaUpToDateHealthCheck"/> (CORE-OBS-010) — the readiness check that gates on the
/// database schema being up to date, bounded and fail-closed. Using a fake probe (no real database) they pin the
/// behavior the acceptance criteria require provider-independently: schema current => Ready; a missing migration
/// => not-ready (so a host rolled forward ahead of its schema leaves the rotation); a read that throws OR exceeds
/// the short timeout is counted as not-ready (fail-closed, CORE-RES-004/005); and the failure description leaks no
/// schema detail (threat T7). Generic, product-neutral vocabulary only (AGENTS.md). The end-to-end "apply
/// all-but-the-last migration, then the last" flip is covered against real migrations by the PostgreSQL
/// integration test (SchemaReadinessHealthCheckTests).
/// </summary>
public class SchemaUpToDateHealthCheckTests
{
    private static readonly DependencyReadinessOptions _fastTimeout =
        new(TimeSpan.FromMilliseconds(150));

    private static Task<HealthCheckResult> CheckAsync(
        ISchemaMigrationProbe probe, DependencyReadinessOptions? options = null)
        => new SchemaUpToDateHealthCheck(probe, options ?? DependencyReadinessOptions.Default)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task Reports_healthy_when_the_schema_is_up_to_date()
    {
        var result = await CheckAsync(FakeProbe.SchemaCurrent());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Reports_unhealthy_when_a_migration_is_missing()
    {
        var result = await CheckAsync(FakeProbe.SchemaBehind());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        // Status leaves the host out of rotation; the description never names a migration (threat T7).
        Assert.DoesNotContain("2026", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_unhealthy_and_fails_closed_when_the_read_throws()
    {
        // A database that is unreachable (or any read failure) leaves the schema state unknown: fail closed.
        var result = await CheckAsync(FakeProbe.Throws());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task A_hung_read_is_bounded_by_the_short_timeout_and_counted_not_ready()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await CheckAsync(FakeProbe.Hangs(), _fastTimeout);

        stopwatch.Stop();

        // Fail-closed: a read that never returns is treated as not-ready, and the readiness response is bounded
        // by the short timeout rather than hanging (CORE-RES-004/005).
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"The readiness check should have been bounded by the short timeout, but it took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SchemaUpToDateHealthCheck(null!, DependencyReadinessOptions.Default));
        Assert.Throws<ArgumentNullException>(
            () => new SchemaUpToDateHealthCheck(FakeProbe.SchemaCurrent(), null!));
        await Task.CompletedTask;
    }

    private sealed class FakeProbe : ISchemaMigrationProbe
    {
        private readonly Func<CancellationToken, Task<bool>> _probe;

        private FakeProbe(Func<CancellationToken, Task<bool>> probe) => _probe = probe;

        public Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken) => _probe(cancellationToken);

        public static FakeProbe SchemaCurrent() => new(_ => Task.FromResult(false));

        public static FakeProbe SchemaBehind() => new(_ => Task.FromResult(true));

        public static FakeProbe Throws() =>
            new(_ => throw new InvalidOperationException("database unreachable"));

        public static FakeProbe Hangs() =>
            new(async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return false;
            });
    }
}
