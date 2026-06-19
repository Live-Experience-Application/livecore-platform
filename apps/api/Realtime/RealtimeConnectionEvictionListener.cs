// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// The hosted service that wires the cross-instance realtime-connection eviction receive side (CORE-RES-008). At
/// startup it subscribes to the <see cref="IRealtimeConnectionEvictionBackplane"/> and routes every eviction
/// descriptor a PEER replica publishes into this instance's <see cref="RealtimeConnectionRegistry.ApplyRemoteEviction"/>,
/// so a demotion/removal handled on any replica aborts the matching still-open socket on THIS replica too.
///
/// <para>
/// It is registered only when the backplane is active (a connection string is configured AND the toggle is on;
/// <see cref="RealtimeConnectionEvictionOptions.IsActive"/>). The handler applies the eviction LOCALLY ONLY and never
/// re-publishes, so a received descriptor cannot echo back across the backplane. It carries only opaque surrogate ids
/// (<see cref="RealtimeConnectionEviction"/>), never content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). It is
/// the realtime-connection counterpart of the <c>AuthorizationCacheInvalidationListener</c> (CORE-RES-007).
/// </para>
/// </summary>
internal sealed class RealtimeConnectionEvictionListener : IHostedService
{
    private readonly IRealtimeConnectionEvictionBackplane _backplane;
    private readonly RealtimeConnectionRegistry _registry;

    public RealtimeConnectionEvictionListener(
        IRealtimeConnectionEvictionBackplane backplane,
        RealtimeConnectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(backplane);
        ArgumentNullException.ThrowIfNull(registry);
        _backplane = backplane;
        _registry = registry;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Apply a peer's eviction to THIS instance's held connections only; ApplyRemoteEviction never re-publishes.
        _backplane.Subscribe(_registry.ApplyRemoteEviction);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
