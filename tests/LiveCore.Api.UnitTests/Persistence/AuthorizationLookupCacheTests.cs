// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="AuthorizationLookupCache"/> (CORE-PERF-003), the shared short-TTL cache that serves
/// the stable per-request authorization lookups so a high request rate does not re-issue them.
///
/// The behaviours that keep the cache from ever changing an authorization decision are mandatory here:
/// a positive lookup is served from the cache (the query is not re-issued within the TTL); a MISS is never cached
/// (so a foreign-tenant / unauthorized denial is always re-checked, fail-closed); invalidating a subject or
/// organization group evicts exactly the entries that named it (so a membership change revokes cached access on the
/// next lookup); and a disabled cache always goes to the loader.
/// </summary>
public sealed class AuthorizationLookupCacheTests
{
    private sealed record Entry(Guid OrganizationId, Guid SubjectId);

    private static AuthorizationLookupCache CreateCache(bool enabled = true, TimeSpan? ttl = null)
        => new(
            new MemoryCache(new MemoryCacheOptions()),
            new AuthorizationCacheOptions(enabled, ttl ?? TimeSpan.FromMinutes(5)));

    private static Func<Entry, IReadOnlyList<string>> Groups()
        => entry =>
        [
            AuthorizationLookupCache.OrganizationGroup(entry.OrganizationId),
            AuthorizationLookupCache.SubjectGroup(entry.SubjectId),
        ];

    [Fact]
    public async Task Serves_a_positive_result_from_cache_without_reloading_within_the_ttl()
    {
        var cache = CreateCache();
        var loads = 0;
        var entry = new Entry(Guid.CreateVersion7(), Guid.CreateVersion7());

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        var first = await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        var second = await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        var third = await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);

        Assert.Equal(1, loads);
        Assert.Same(entry, first);
        Assert.Same(entry, second);
        Assert.Same(entry, third);
    }

    [Fact]
    public async Task Never_caches_a_miss_so_a_denial_is_always_rechecked()
    {
        var cache = CreateCache();
        var loads = 0;

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(null);
        }

        var first = await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        var second = await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        // A null (miss) is positive-only-not-cached, so every call re-evaluates against the loader (fail-closed).
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Invalidating_the_subject_group_evicts_and_forces_a_reload()
    {
        var cache = CreateCache();
        var loads = 0;
        var subjectId = Guid.CreateVersion7();
        var entry = new Entry(Guid.CreateVersion7(), subjectId);

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        Assert.Equal(1, loads);

        cache.InvalidateSubject(subjectId);

        await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Invalidating_the_organization_group_evicts_and_forces_a_reload()
    {
        var cache = CreateCache();
        var loads = 0;
        var organizationId = Guid.CreateVersion7();
        var entry = new Entry(organizationId, Guid.CreateVersion7());

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        Assert.Equal(1, loads);

        cache.InvalidateOrganization(organizationId);

        await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Invalidating_an_unrelated_subject_does_not_evict()
    {
        var cache = CreateCache();
        var loads = 0;
        var entry = new Entry(Guid.CreateVersion7(), Guid.CreateVersion7());

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        cache.InvalidateSubject(Guid.CreateVersion7());
        await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);

        // A different subject's invalidation must not evict this entry.
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Disabled_cache_always_invokes_the_loader_and_caches_nothing()
    {
        var cache = CreateCache(enabled: false);
        var loads = 0;
        var entry = new Entry(Guid.CreateVersion7(), Guid.CreateVersion7());

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        var first = await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);
        var second = await cache.GetOrAddAsync("key", Load, Groups(), CancellationToken.None);

        Assert.Same(entry, first);
        Assert.Same(entry, second);
        Assert.Equal(2, loads);
    }
}
