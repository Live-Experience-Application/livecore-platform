// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Persistence;

/// <summary>
/// The cross-instance seam for the per-process authorization-lookup cache (CORE-RES-007, the "Multi-Instance
/// Runtime Correctness" epic). <see cref="AuthorizationLookupCache"/> is a per-process <c>IMemoryCache</c>, so on its
/// own a membership/role revocation invalidates only the cache of the instance that handled the change; the other
/// API replicas keep serving the cached grant until their entry expires (the <see cref="AuthorizationCacheOptions.Ttl"/>
/// backstop, ~10s by default). This seam closes that window: on a local revocation the cache PUBLISHES the affected
/// invalidation group over the deployment's configured Valkey/Redis backplane (the same backplane the realtime
/// scale-out uses, docs/02_ARCHITECTURE.md / docs/11_REALTIME_SYNC.md), and every replica's
/// <see cref="AuthorizationCacheInvalidationListener"/> applies it to its OWN cache, so the revocation takes effect
/// across all replicas within a bounded, documented window instead of lingering until the TTL.
///
/// <para>
/// CORRECTNESS / FAIL-CLOSED (the story's "no new fail-open path"). The cross-instance broadcast can only ever cause
/// MORE invalidation, never less, so it cannot widen access:
/// </para>
///
/// <list type="number">
///   <item><b>Local eviction is unconditional and first.</b> The originating instance evicts its own cache BEFORE it
///   publishes, so a failed/dropped broadcast leaves the local revocation intact and the other replicas simply fall
///   back to the TTL backstop — a smaller window, never a stale-serve forever. <see cref="Publish"/> is best-effort
///   and MUST NOT throw (a broadcast failure must never turn a successful revocation into a request error).</item>
///   <item><b>Positive-cache only is unchanged.</b> The cache still only ever holds POSITIVE lookups; this seam only
///   evicts them earlier on peer replicas. A denial is still always re-evaluated against the database, fail-closed.</item>
///   <item><b>Apply-only on receipt.</b> A received invalidation is applied LOCALLY ONLY
///   (<see cref="AuthorizationLookupCache.ApplyRemoteInvalidation"/>) and never re-published, so it cannot echo
///   forever across the backplane.</item>
/// </list>
///
/// <para>
/// The message carries only an opaque invalidation-group token (a surrogate subject/organization id, e.g.
/// <c>s:&lt;guid&gt;</c> / <c>o:&lt;guid&gt;</c>) — never a tenant name, principal detail or content (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). With no backplane configured the host uses the
/// <see cref="NullAuthorizationCacheInvalidationBackplane"/> no-op, so single-instance behaviour is exactly as
/// before (local eviction plus the TTL backstop).
/// </para>
/// </summary>
internal interface IAuthorizationCacheInvalidationBackplane
{
    /// <summary>
    /// Best-effort broadcasts a local cache invalidation of <paramref name="invalidationGroup"/> to the other API
    /// replicas over the configured backplane. MUST NOT throw: a broadcast failure falls back to the TTL backstop
    /// (the local eviction has already happened), so it never breaks the revocation that triggered it.
    /// </summary>
    void Publish(string invalidationGroup);

    /// <summary>
    /// Subscribes <paramref name="onRemoteInvalidation"/> to invalidation groups other replicas publish, so this
    /// instance can evict the matching entries from its own cache. Called once at startup by the
    /// <see cref="AuthorizationCacheInvalidationListener"/>.
    /// </summary>
    void Subscribe(Action<string> onRemoteInvalidation);
}

/// <summary>
/// The no-op <see cref="IAuthorizationCacheInvalidationBackplane"/> used when no backplane is configured (a
/// single-instance deployment, the documented single-instance constraint). It neither publishes nor receives, so the
/// authorization cache behaves exactly as it did before CORE-RES-007: a revocation evicts the local cache
/// immediately and the <see cref="AuthorizationCacheOptions.Ttl"/> bounds the window for any (non-existent) peer.
/// </summary>
internal sealed class NullAuthorizationCacheInvalidationBackplane : IAuthorizationCacheInvalidationBackplane
{
    /// <summary>The shared, stateless no-op instance.</summary>
    public static NullAuthorizationCacheInvalidationBackplane Instance { get; } = new();

    /// <inheritdoc />
    public void Publish(string invalidationGroup)
    {
        // No backplane configured: nothing to broadcast to. The local eviction already happened in the cache.
    }

    /// <inheritdoc />
    public void Subscribe(Action<string> onRemoteInvalidation)
    {
        // No backplane configured: no peer publishes, so there is nothing to receive.
    }
}
