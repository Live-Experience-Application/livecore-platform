// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="AuthorizationCacheInvalidationListener"/> (CORE-RES-007): the hosted service that wires
/// the cross-instance invalidation RECEIVE side. On start it must subscribe to the backplane and route every group a
/// peer replica publishes into THIS instance's <see cref="AuthorizationLookupCache.ApplyRemoteInvalidation"/>, so a
/// revocation broadcast by another replica evicts the matching cached authorization here too. A fake backplane
/// captures the subscribed handler so the test can drive a "received message" deterministically without a real
/// server. Generic vocabulary only (AGENTS.md).
/// </summary>
public sealed class AuthorizationCacheInvalidationListenerTests
{
    private sealed record Entry(Guid OrganizationId, Guid SubjectId);

    [Fact]
    public async Task On_start_it_subscribes_and_routes_a_received_group_into_the_local_cache()
    {
        var backplane = new CapturingBackplane();
        var cache = new AuthorizationLookupCache(
            new MemoryCache(new MemoryCacheOptions()),
            new AuthorizationCacheOptions(enabled: true, ttl: TimeSpan.FromMinutes(5)),
            backplane);
        var listener = new AuthorizationCacheInvalidationListener(backplane, cache);

        var subjectId = Guid.CreateVersion7();
        var entry = new Entry(Guid.CreateVersion7(), subjectId);
        var loads = 0;

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        Func<Entry, IReadOnlyList<string>> groups = e => [AuthorizationLookupCache.SubjectGroup(e.SubjectId)];

        await cache.GetOrAddAsync("k", Load, groups, CancellationToken.None);
        Assert.Equal(1, loads);

        // Before start there is no subscriber.
        Assert.Null(backplane.Handler);

        await listener.StartAsync(CancellationToken.None);
        Assert.NotNull(backplane.Handler);

        // A peer replica publishes the subject's invalidation; the listener routes it into this instance's cache.
        backplane.Handler!(AuthorizationLookupCache.SubjectGroup(subjectId));

        await cache.GetOrAddAsync("k", Load, groups, CancellationToken.None);
        Assert.Equal(2, loads);

        await listener.StopAsync(CancellationToken.None);
    }

    /// <summary>A backplane that captures the single handler the listener subscribes, so the test can invoke it.</summary>
    private sealed class CapturingBackplane : IAuthorizationCacheInvalidationBackplane
    {
        public Action<string>? Handler { get; private set; }

        public void Publish(string invalidationGroup)
        {
            // Not exercised by these tests.
        }

        public void Subscribe(Action<string> onRemoteInvalidation) => Handler = onRemoteInvalidation;
    }
}
