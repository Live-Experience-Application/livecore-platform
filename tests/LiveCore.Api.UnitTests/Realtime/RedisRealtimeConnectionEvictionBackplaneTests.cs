// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Lifecycle unit tests for <see cref="RedisRealtimeConnectionEvictionBackplane"/> (CORE-RES-008) that need NO real
/// server. They construct the backplane against an UNREACHABLE endpoint — which is exactly the fail-safe startup
/// posture (<c>AbortOnConnectFail = false</c>, so a briefly-unreachable backplane never fails host startup) — and
/// assert that publishing is BEST-EFFORT: it never throws even when the backplane cannot be reached, so a broadcast
/// failure can never turn a successful local eviction into a request error (no new fail-open path). The real
/// cross-instance delivery over a live server is covered by the skipped-by-default integration test.
/// </summary>
public sealed class RedisRealtimeConnectionEvictionBackplaneTests
{
    private static RealtimeConnectionEvictionOptions UnreachableOptions()
        => RealtimeConnectionEvictionOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // A closed port: the multiplexer connects in the background and never succeeds, modeling a
                    // briefly/permanently unreachable backplane. connectRetry=0 keeps the test snappy.
                    ["Realtime:Backplane:ConnectionString"] = "127.0.0.1:1,connectTimeout=200,connectRetry=0",
                })
                .Build());

    [Fact]
    public void Construction_does_not_throw_when_the_backplane_is_unreachable()
    {
        // Fail-safe startup: AbortOnConnectFail=false means a missing backplane never blocks/fails construction.
        using var backplane = new RedisRealtimeConnectionEvictionBackplane(UnreachableOptions());
        Assert.NotNull(backplane);
    }

    [Fact]
    public void Publish_is_best_effort_and_never_throws_when_the_backplane_is_unreachable()
    {
        using var backplane = new RedisRealtimeConnectionEvictionBackplane(UnreachableOptions());

        // The local eviction already happened in the registry; a failed broadcast must never surface as an exception.
        var token = RealtimeConnectionEviction
            .ForWorkspaceMember(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7())
            .Serialize();
        var exception = Record.Exception(() => backplane.Publish(token));

        Assert.Null(exception);
    }

    [Fact]
    public void Subscribe_registers_a_handler_without_throwing()
    {
        using var backplane = new RedisRealtimeConnectionEvictionBackplane(UnreachableOptions());

        var exception = Record.Exception(() => backplane.Subscribe(_ => { }));

        Assert.Null(exception);
    }

    [Fact]
    public void Construction_rejects_a_missing_connection_string()
    {
        var options = RealtimeConnectionEvictionOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Throws<ArgumentException>(() => new RedisRealtimeConnectionEvictionBackplane(options));
    }
}
