// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace LiveCore.Api.Persistence;

/// <summary>
/// The shared, process-wide cache for the STABLE per-request authorization lookups (CORE-PERF-003, the
/// "Performance and Scalability" epic). Every authenticated request runs the tenant context resolver — three
/// sequential lookups (organization-by-slug, user-profile-by-OIDC, organization-membership) — before the
/// endpoint, and many endpoints then re-query the caller's workspace membership/role; at a high request rate the
/// SAME principal re-issues exactly those queries on every request and hub connect. This cache serves them from a
/// short-TTL in-memory store (<see cref="AuthorizationCacheOptions"/>) so they are not re-issued within the
/// window, then is INVALIDATED explicitly on every membership change so authorization stays correct.
///
/// <para>
/// CORRECTNESS / FAIL-CLOSED (the epic's "correctness/fail-closed authz is unchanged" criterion). Two properties
/// keep the cache from ever changing an authorization decision:
/// </para>
///
/// <list type="number">
///   <item><b>Positive-only.</b> Only a HIT — an organization that exists, a known profile, an existing
///   membership — is cached; a MISS (no organization, unknown subject, no membership) is never stored, so a
///   foreign-tenant / unauthorized-role denial is always re-evaluated against the database and can never be served
///   stale from a cached "no". Equally, a just-granted membership is observed on the very next request because no
///   negative was cached to shadow it.</item>
///   <item><b>Explicit invalidation on every membership change.</b> Every cached entry is linked to the SUBJECT
///   and ORGANIZATION it concerns (see <see cref="SubjectGroup"/> / <see cref="OrganizationGroup"/>); a membership
///   change cancels the corresponding group, evicting every cached lookup it could have affected — including the
///   ones a database CASCADE removed without going through a repository (a data-subject erasure or a tenant
///   deletion). Removing a membership therefore revokes the subject's cached access on their very next request,
///   fail-closed, exactly as the un-cached path did.</item>
/// </list>
///
/// <para>
/// The cache is consulted only inside the persistence layer's caching repository decorators (it is registered only
/// when persistence is configured), and the token checks the resolver performs BEFORE any database access — the
/// organization claim on the bearer token — are never cached, so a request whose token is not scoped to the tenant
/// is still denied per request. The cache holds only surrogate identifiers and authorization metadata, never
/// free-form content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
///
/// <para>
/// CROSS-INSTANCE INVALIDATION (CORE-RES-007, the "Multi-Instance Runtime Correctness" epic). The cache is a
/// PER-PROCESS <c>IMemoryCache</c>, so on its own an <see cref="InvalidateSubject"/> / <see cref="InvalidateOrganization"/>
/// only evicts the instance that handled the revocation; the other API replicas keep serving the cached grant until
/// their entry expires (the <see cref="AuthorizationCacheOptions.Ttl"/> backstop). To take the revocation across all
/// replicas within a bounded window, each invalidation also PUBLISHES the affected group over the configured
/// <see cref="IAuthorizationCacheInvalidationBackplane"/> (the deployment's Valkey/Redis backplane), and every
/// replica's <see cref="AuthorizationCacheInvalidationListener"/> feeds received groups into
/// <see cref="ApplyRemoteInvalidation"/>. The local eviction happens FIRST and unconditionally, so a dropped
/// broadcast only falls back to the TTL backstop — never a stale serve forever, and never a request error (the
/// broadcast is best-effort; no new fail-open path). With no backplane configured the
/// <see cref="NullAuthorizationCacheInvalidationBackplane"/> no-op keeps single-instance behaviour unchanged.
/// </para>
/// </summary>
internal sealed class AuthorizationLookupCache : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly AuthorizationCacheOptions _options;
    private readonly IAuthorizationCacheInvalidationBackplane _backplane;

    // One cancellation source per invalidation GROUP (a subject or an organization). Every cache entry links a
    // change token from each group it concerns, so cancelling a group's source evicts every entry that named it.
    // A cancelled source is removed so the next entry for that group starts a fresh, un-cancelled source.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _groupTokens =
        new(StringComparer.Ordinal);

    public AuthorizationLookupCache(
        IMemoryCache cache,
        AuthorizationCacheOptions options,
        IAuthorizationCacheInvalidationBackplane? backplane = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        _cache = cache;
        _options = options;
        // No backplane configured (single instance / tests) => the no-op: local eviction plus the TTL backstop only.
        _backplane = backplane ?? NullAuthorizationCacheInvalidationBackplane.Instance;
    }

    /// <summary>Whether caching is enabled; when off, every lookup goes straight to the loader and nothing is stored.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or invokes <paramref name="load"/> and caches a
    /// NON-NULL result under the short TTL, linked to the invalidation groups <paramref name="invalidationGroups"/>
    /// derives from the loaded value (its subject and/or organization). A null result (a miss) is never cached —
    /// only a positive lookup is, so a denial is always re-evaluated (fail-closed). When the cache is disabled the
    /// loader is always invoked and nothing is stored.
    /// </summary>
    /// <typeparam name="T">The cached value type (a reference type; null marks a miss that is not cached).</typeparam>
    /// <param name="key">The unique cache key for this lookup.</param>
    /// <param name="load">The database-backed loader, invoked only on a cache miss.</param>
    /// <param name="invalidationGroups">
    /// Selects the groups whose cancellation must evict this entry, FROM the loaded value (so the entry can be keyed
    /// to the subject and organization it actually resolved to — e.g. an organization id that is only known after
    /// the by-slug lookup runs).
    /// </param>
    /// <param name="cancellationToken">Cancellation token passed to the loader.</param>
    public async Task<T?> GetOrAddAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> load,
        Func<T, IReadOnlyList<string>> invalidationGroups,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(invalidationGroups);

        if (!_options.Enabled)
        {
            return await load(cancellationToken).ConfigureAwait(false);
        }

        if (_cache.TryGetValue(key, out T? cached))
        {
            return cached;
        }

        var loaded = await load(cancellationToken).ConfigureAwait(false);

        // Positive-only: a miss is never cached, so a foreign-tenant / unauthorized denial is always re-checked.
        if (loaded is null)
        {
            return null;
        }

        var entryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _options.Ttl,
        };

        // Link a change token from each group the loaded value belongs to. If a group was cancelled concurrently (a
        // membership change racing this set), the token is already signalled and the entry is born expired — a
        // re-query, never a stale serve. This is the desired fail-safe outcome: a concurrent invalidation wins.
        foreach (var group in invalidationGroups(loaded))
        {
            entryOptions.AddExpirationToken(new CancellationChangeToken(GroupTokenSource(group).Token));
        }

        _cache.Set(key, loaded, entryOptions);
        return loaded;
    }

    /// <summary>
    /// Invalidates every cached lookup that concerns the given subject (their profile, organization memberships and
    /// workspace memberships/roles). Called on any change to the subject's standing — an organization or workspace
    /// membership removal, or a data-subject erasure whose database cascade revoked their memberships. The eviction
    /// is also BROADCAST cross-instance (CORE-RES-007), so the revocation takes effect on every API replica, not only
    /// this one.
    /// </summary>
    public void InvalidateSubject(Guid userProfileId) => InvalidateGroup(SubjectGroup(userProfileId));

    /// <summary>
    /// Invalidates every cached lookup that concerns the given organization (its by-slug lookup and all of its
    /// memberships/roles). Called when the tenant is deleted, whose database cascade removed every membership in it.
    /// The eviction is also BROADCAST cross-instance (CORE-RES-007), so the deletion takes effect on every API replica.
    /// </summary>
    public void InvalidateOrganization(Guid organizationId) => InvalidateGroup(OrganizationGroup(organizationId));

    /// <summary>
    /// Applies an invalidation a PEER replica broadcast (CORE-RES-007): evicts the group from THIS instance's cache
    /// only. It deliberately does NOT re-broadcast (that would echo forever across the backplane), and it ignores a
    /// malformed or unrecognized token defensively, so a stray message can never evict an unintended group. Called by
    /// the <see cref="AuthorizationCacheInvalidationListener"/> on every message received from the backplane.
    /// </summary>
    public void ApplyRemoteInvalidation(string invalidationGroup)
    {
        if (IsInvalidationGroup(invalidationGroup))
        {
            // Local-only: do NOT publish, or a received invalidation would echo back across the backplane forever.
            CancelGroup(invalidationGroup);
        }
    }

    /// <summary>The invalidation-group name for a subject's cached lookups.</summary>
    internal static string SubjectGroup(Guid userProfileId) => string.Concat("s:", userProfileId.ToString("N"));

    /// <summary>The invalidation-group name for an organization's cached lookups.</summary>
    internal static string OrganizationGroup(Guid organizationId) => string.Concat("o:", organizationId.ToString("N"));

    // "s:"/"o:" prefix + a 32-char "N"-format Guid = 34 chars. Validating the shape defends ApplyRemoteInvalidation
    // against a malformed or unexpected message on the shared backplane channel (it must never evict an unintended
    // group), and keeps the wire format identical to the local group names so a peer's token feeds straight in.
    internal static bool IsInvalidationGroup(string? group)
        => group is { Length: 34 }
            && (group[0] is 's' or 'o')
            && group[1] == ':'
            && Guid.TryParseExact(group.AsSpan(2), "N", out _);

    // Evict the group locally FIRST (this instance is correct immediately), then broadcast best-effort so the other
    // replicas evict it too (CORE-RES-007). A failed broadcast falls back to the TTL backstop — never a stale serve.
    private void InvalidateGroup(string group)
    {
        CancelGroup(group);
        _backplane.Publish(group);
    }

    private CancellationTokenSource GroupTokenSource(string group)
        => _groupTokens.GetOrAdd(group, static _ => new CancellationTokenSource());

    private void CancelGroup(string group)
    {
        // Remove first so a subsequent entry for the same group starts a fresh source, then cancel to evict the
        // entries currently linked to it. The source is not disposed here: MemoryCache still holds eviction
        // registrations on its token, and a concurrent GetOrAddAsync may be about to read its Token; it becomes
        // collectable once those entries are evicted. The remaining live sources are disposed in Dispose.
        if (_groupTokens.TryRemove(group, out var source))
        {
            source.Cancel();
        }
    }

    public void Dispose()
    {
        foreach (var source in _groupTokens.Values)
        {
            source.Dispose();
        }

        _groupTokens.Clear();
    }
}
