// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Persistence;

/// <summary>
/// The hosted service that wires the cross-instance authorization-cache invalidation receive side (CORE-RES-007). At
/// startup it subscribes to the <see cref="IAuthorizationCacheInvalidationBackplane"/> and routes every invalidation
/// group a PEER replica publishes into this instance's <see cref="AuthorizationLookupCache.ApplyRemoteInvalidation"/>,
/// so a revocation on any replica evicts the matching cached authorization on THIS replica too.
///
/// <para>
/// It is registered only when the backplane is active (a connection string is configured AND the toggle is on;
/// <see cref="AuthorizationCacheInvalidationOptions.IsActive"/>). The handler applies the invalidation LOCALLY ONLY
/// and never re-publishes, so a received message cannot echo back across the backplane. It carries only an opaque
/// invalidation-group token (a surrogate id), never content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
/// </summary>
internal sealed class AuthorizationCacheInvalidationListener : IHostedService
{
    private readonly IAuthorizationCacheInvalidationBackplane _backplane;
    private readonly AuthorizationLookupCache _cache;

    public AuthorizationCacheInvalidationListener(
        IAuthorizationCacheInvalidationBackplane backplane,
        AuthorizationLookupCache cache)
    {
        ArgumentNullException.ThrowIfNull(backplane);
        ArgumentNullException.ThrowIfNull(cache);
        _backplane = backplane;
        _cache = cache;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Apply a peer's invalidation to THIS instance's cache only; ApplyRemoteInvalidation never re-publishes.
        _backplane.Subscribe(_cache.ApplyRemoteInvalidation);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
