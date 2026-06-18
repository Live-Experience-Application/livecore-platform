// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for the cross-instance authorization-cache invalidation (CORE-RES-007, the "Multi-Instance Runtime
/// Correctness" epic). The authorization-lookup cache is a PER-PROCESS <see cref="AuthorizationLookupCache"/>, so a
/// revocation handled on one API replica must be BROADCAST so the other replicas evict the same cached grant; until
/// then the only bound is the TTL backstop.
///
/// These tests model TWO logical instances with their OWN <c>IMemoryCache</c> sharing one backplane (an in-memory
/// fake standing in for the Valkey/Redis pub/sub server, so the cross-instance contract is proven deterministically
/// without a real server — the real transport is covered by the skipped-by-default integration test). They assert:
/// <list type="bullet">
///   <item>a revocation on instance A evicts the cached authorization on a SECOND instance B, which then re-queries
///   and DENIES — fail-closed, the revocation took effect across replicas;</item>
///   <item>a received invalidation is applied LOCALLY ONLY and never re-broadcast (no echo loop);</item>
///   <item>each local invalidation broadcasts exactly the affected group once;</item>
///   <item>a malformed/unrecognized message evicts nothing (defensive), and a well-formed one evicts its group;</item>
///   <item>the no-op backplane keeps single-instance behaviour unchanged.</item>
/// </list>
/// Generic vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AuthorizationCacheInvalidationTests
{
    private sealed record Entry(Guid OrganizationId, Guid SubjectId);

    private static AuthorizationLookupCache CreateCache(IAuthorizationCacheInvalidationBackplane? backplane = null)
        => new(
            new MemoryCache(new MemoryCacheOptions()),
            new AuthorizationCacheOptions(enabled: true, ttl: TimeSpan.FromMinutes(5)),
            backplane);

    private static Func<Entry, IReadOnlyList<string>> Groups()
        => entry =>
        [
            AuthorizationLookupCache.OrganizationGroup(entry.OrganizationId),
            AuthorizationLookupCache.SubjectGroup(entry.SubjectId),
        ];

    [Fact]
    public async Task A_revocation_on_one_instance_evicts_the_cached_authorization_on_a_second_instance()
    {
        // The modeled Valkey/Redis server shared by both replicas: a publish fans out to every subscriber.
        var backplane = new FakeInvalidationBackplane();
        var subjectId = Guid.CreateVersion7();
        var entry = new Entry(Guid.CreateVersion7(), subjectId);

        var instanceA = CreateCache(backplane);
        var instanceB = CreateCache(backplane);

        // Each replica's listener applies a peer's invalidation to its OWN cache (the production wiring).
        backplane.Subscribe(instanceA.ApplyRemoteInvalidation);
        backplane.Subscribe(instanceB.ApplyRemoteInvalidation);

        var revoked = false;
        var loadsB = 0;

        Task<Entry?> LoadB(CancellationToken _)
        {
            loadsB++;
            return Task.FromResult(revoked ? null : entry);
        }

        // Instance B caches the positive membership and serves it from cache within the TTL.
        Assert.NotNull(await instanceB.GetOrAddAsync("k", LoadB, Groups(), CancellationToken.None));
        Assert.NotNull(await instanceB.GetOrAddAsync("k", LoadB, Groups(), CancellationToken.None));
        Assert.Equal(1, loadsB);

        // The membership is revoked in the database and instance A (a DIFFERENT replica) handles the revocation.
        revoked = true;
        instanceA.InvalidateSubject(subjectId);

        // The broadcast crossed to instance B: its cached grant is gone, so its next lookup re-queries the database
        // and now DENIES. The revocation took effect on the second instance, fail-closed.
        Assert.Null(await instanceB.GetOrAddAsync("k", LoadB, Groups(), CancellationToken.None));
        Assert.Equal(2, loadsB);
    }

    [Fact]
    public async Task An_organization_deletion_on_one_instance_evicts_the_tenant_on_a_second_instance()
    {
        var backplane = new FakeInvalidationBackplane();
        var organizationId = Guid.CreateVersion7();
        var entry = new Entry(organizationId, Guid.CreateVersion7());

        var instanceA = CreateCache(backplane);
        var instanceB = CreateCache(backplane);
        backplane.Subscribe(instanceA.ApplyRemoteInvalidation);
        backplane.Subscribe(instanceB.ApplyRemoteInvalidation);

        var deleted = false;
        var loadsB = 0;

        Task<Entry?> LoadB(CancellationToken _)
        {
            loadsB++;
            return Task.FromResult(deleted ? null : entry);
        }

        Assert.NotNull(await instanceB.GetOrAddAsync("k", LoadB, Groups(), CancellationToken.None));
        Assert.Equal(1, loadsB);

        deleted = true;
        instanceA.InvalidateOrganization(organizationId);

        Assert.Null(await instanceB.GetOrAddAsync("k", LoadB, Groups(), CancellationToken.None));
        Assert.Equal(2, loadsB);
    }

    [Fact]
    public void A_received_invalidation_is_applied_locally_and_never_rebroadcast()
    {
        var backplane = new FakeInvalidationBackplane();
        var instance = CreateCache(backplane);
        backplane.Subscribe(instance.ApplyRemoteInvalidation);

        instance.ApplyRemoteInvalidation(AuthorizationLookupCache.SubjectGroup(Guid.CreateVersion7()));

        // Applying a peer's invalidation must NOT publish anything, or it would echo across the backplane forever.
        Assert.Empty(backplane.Published);
    }

    [Fact]
    public void Invalidating_a_subject_broadcasts_exactly_that_group_once()
    {
        var backplane = new FakeInvalidationBackplane();
        var cache = CreateCache(backplane);
        var subjectId = Guid.CreateVersion7();

        cache.InvalidateSubject(subjectId);

        Assert.Equal(new[] { AuthorizationLookupCache.SubjectGroup(subjectId) }, backplane.Published);
    }

    [Fact]
    public void Invalidating_an_organization_broadcasts_exactly_that_group_once()
    {
        var backplane = new FakeInvalidationBackplane();
        var cache = CreateCache(backplane);
        var organizationId = Guid.CreateVersion7();

        cache.InvalidateOrganization(organizationId);

        Assert.Equal(new[] { AuthorizationLookupCache.OrganizationGroup(organizationId) }, backplane.Published);
    }

    [Fact]
    public async Task A_valid_remote_invalidation_evicts_the_named_group_and_forces_a_reload()
    {
        var cache = CreateCache();
        var subjectId = Guid.CreateVersion7();
        var entry = new Entry(Guid.CreateVersion7(), subjectId);
        var loads = 0;

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        await cache.GetOrAddAsync("k", Load, Groups(), CancellationToken.None);
        Assert.Equal(1, loads);

        cache.ApplyRemoteInvalidation(AuthorizationLookupCache.SubjectGroup(subjectId));

        await cache.GetOrAddAsync("k", Load, Groups(), CancellationToken.None);
        Assert.Equal(2, loads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-group")]
    [InlineData("x:00000000000000000000000000000000")] // unknown prefix
    [InlineData("s:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]   // right length (34), but not hex — not a Guid
    [InlineData("s:0000000000000000000000000000000")]   // 31 hex chars (too short)
    public async Task A_malformed_remote_invalidation_evicts_nothing(string? group)
    {
        var cache = CreateCache();
        var entry = new Entry(Guid.CreateVersion7(), Guid.CreateVersion7());
        var loads = 0;

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        await cache.GetOrAddAsync("k", Load, Groups(), CancellationToken.None);

        cache.ApplyRemoteInvalidation(group!);

        // A malformed/unrecognized token must never evict an unintended group — the entry is still served from cache.
        await cache.GetOrAddAsync("k", Load, Groups(), CancellationToken.None);
        Assert.Equal(1, loads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsInvalidationGroup_recognizes_the_subject_and_organization_group_shapes(bool subject)
    {
        var id = Guid.CreateVersion7();
        var group = subject
            ? AuthorizationLookupCache.SubjectGroup(id)
            : AuthorizationLookupCache.OrganizationGroup(id);

        Assert.True(AuthorizationLookupCache.IsInvalidationGroup(group));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s")]
    [InlineData("z:00000000000000000000000000000000")]
    [InlineData("s-00000000000000000000000000000000")]
    public void IsInvalidationGroup_rejects_a_malformed_token(string? group)
        => Assert.False(AuthorizationLookupCache.IsInvalidationGroup(group));

    [Fact]
    public async Task The_null_backplane_keeps_single_instance_behaviour_unchanged()
    {
        // No backplane (the single-instance default): a revocation still evicts the LOCAL cache, and nothing throws.
        var cache = CreateCache();
        var subjectId = Guid.CreateVersion7();
        var entry = new Entry(Guid.CreateVersion7(), subjectId);
        var loads = 0;

        Task<Entry?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<Entry?>(entry);
        }

        await cache.GetOrAddAsync("k", Load, Groups(), CancellationToken.None);
        Assert.Equal(1, loads);

        cache.InvalidateSubject(subjectId);

        await cache.GetOrAddAsync("k", Load, Groups(), CancellationToken.None);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void The_null_backplane_publish_and_subscribe_are_no_ops()
    {
        var backplane = NullAuthorizationCacheInvalidationBackplane.Instance;

        // No backplane configured: neither call does anything (and neither throws).
        backplane.Publish(AuthorizationLookupCache.SubjectGroup(Guid.CreateVersion7()));
        backplane.Subscribe(_ => throw new InvalidOperationException("a no-op backplane must never invoke a handler"));
    }

    /// <summary>
    /// An in-memory stand-in for the Valkey/Redis pub/sub server shared by the modeled replicas: a publish fans out
    /// to every subscribed handler (as Redis delivers a published message to every subscriber, including the
    /// publisher's own instance). It records what was published so a test can assert the broadcast shape.
    /// </summary>
    private sealed class FakeInvalidationBackplane : IAuthorizationCacheInvalidationBackplane
    {
        private readonly List<Action<string>> _handlers = [];

        public List<string> Published { get; } = [];

        public void Publish(string invalidationGroup)
        {
            Published.Add(invalidationGroup);
            foreach (var handler in _handlers.ToArray())
            {
                handler(invalidationGroup);
            }
        }

        public void Subscribe(Action<string> onRemoteInvalidation) => _handlers.Add(onRemoteInvalidation);
    }
}
