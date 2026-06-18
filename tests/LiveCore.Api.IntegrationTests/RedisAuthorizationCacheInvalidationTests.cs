// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// CROSS-INSTANCE authorization-cache invalidation over the REAL Valkey/Redis backplane (CORE-RES-007, the
/// "Multi-Instance Runtime Correctness" epic). It models TWO logical API replicas as two
/// <see cref="AuthorizationLookupCache"/> instances — each with its OWN <c>IMemoryCache</c> and its OWN
/// <see cref="RedisAuthorizationCacheInvalidationBackplane"/> connected to the SAME server on the SAME channel — and
/// proves that a revocation handled on one replica evicts the cached authorization on the OTHER replica over real
/// pub/sub, so the revocation takes effect across replicas within a bounded window instead of lingering until the
/// TTL.
///
/// It is SKIPPED unless a Valkey/Redis backplane is supplied out of band (<see cref="RedisBackplaneTestServer.IsConfigured"/>,
/// the CI <c>integration-postgres</c> job's Redis service), exactly like <see cref="RedisBackplanePropagationTests"/>;
/// a default <c>dotnet test</c> needs no server and the deterministic cross-instance contract is proven by the
/// in-memory unit tests. The message carries only an opaque invalidation-group token, never content (threat T7). All
/// fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RedisAuthorizationCacheInvalidationTests
{
    private sealed record Entry(Guid OrganizationId, Guid SubjectId);

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_revocation_on_one_replica_evicts_the_cached_authorization_on_another_replica()
    {
        if (!RedisBackplaneTestServer.IsConfigured)
        {
            return;
        }

        RedisBackplaneTestServer.EnsureReachable();

        // Both replicas use the SAME connection and SAME (unique-per-run) channel prefix, so a publish on one is
        // delivered on the very channel the other subscribes to. A long TTL guarantees the test proves cross-instance
        // INVALIDATION, never reliance on the entry expiring.
        var options = AuthorizationCacheInvalidationOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Realtime:Backplane:ConnectionString"] = RedisBackplaneTestServer.ConnectionString,
                    ["Realtime:Backplane:ChannelPrefix"] = $"livecore-it-authz-{Guid.NewGuid():N}-",
                })
                .Build());
        Assert.True(options.IsActive);

        using var publisherBackplane = new RedisAuthorizationCacheInvalidationBackplane(options);
        using var receiverBackplane = new RedisAuthorizationCacheInvalidationBackplane(options);

        var publisher = new AuthorizationLookupCache(
            new MemoryCache(new MemoryCacheOptions()),
            new AuthorizationCacheOptions(enabled: true, ttl: TimeSpan.FromHours(1)),
            publisherBackplane);
        var receiver = new AuthorizationLookupCache(
            new MemoryCache(new MemoryCacheOptions()),
            new AuthorizationCacheOptions(enabled: true, ttl: TimeSpan.FromHours(1)),
            receiverBackplane);

        // Each replica applies a peer's invalidation to its own cache (the production listener wiring).
        receiverBackplane.Subscribe(receiver.ApplyRemoteInvalidation);
        publisherBackplane.Subscribe(publisher.ApplyRemoteInvalidation);

        var subjectId = Guid.CreateVersion7();
        var entry = new Entry(Guid.CreateVersion7(), subjectId);
        var revoked = false;
        var receiverLoads = 0;

        Task<Entry?> Load(CancellationToken _)
        {
            receiverLoads++;
            return Task.FromResult(revoked ? null : entry);
        }

        Func<Entry, IReadOnlyList<string>> groups = e => [AuthorizationLookupCache.SubjectGroup(e.SubjectId)];

        // The receiver replica caches the positive membership and serves it from cache.
        Assert.NotNull(await receiver.GetOrAddAsync("k", Load, groups, CancellationToken.None));
        Assert.NotNull(await receiver.GetOrAddAsync("k", Load, groups, CancellationToken.None));
        Assert.Equal(1, receiverLoads);

        // The membership is revoked and the PUBLISHER replica handles it, broadcasting over the backplane. Re-publish
        // in the poll loop so the assertion never races the subscription becoming active (an early publish would be
        // dropped — pub/sub has no retention — and re-eviction is idempotent).
        revoked = true;
        var denied = false;
        var deadline = DateTime.UtcNow.Add(_timeout);
        while (!denied && DateTime.UtcNow < deadline)
        {
            publisher.InvalidateSubject(subjectId);
            await Task.Delay(50);
            denied = await receiver.GetOrAddAsync("k", Load, groups, CancellationToken.None) is null;
        }

        // The broadcast crossed the real backplane: the receiver's cached grant was evicted, so its lookup re-queried
        // and now DENIES — the revocation took effect on the other replica, fail-closed.
        Assert.True(denied, "Expected the revocation to evict the cached authorization on the receiver replica.");
        Assert.True(receiverLoads >= 2);
    }
}
