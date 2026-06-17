// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.Realtime;

/// <summary>
/// The <see cref="IRealtimeBackplane"/> implementation (CORE-RT-006): it delivers a computed delivery to the
/// SignalR group by sending over <see cref="IHubContext{SessionHub}"/> (registered by <c>AddSignalR()</c> —
/// part of the ASP.NET Core shared framework, so no new dependency). This is exactly the send the
/// <c>SessionEventPublisher</c> performed inline before the scale-out seam existed. On a single API instance
/// it reaches only that instance's connections; when a deployment configures the SignalR Redis/Valkey
/// HubLifetimeManager (CORE-OPS-007, <see cref="RealtimeServiceCollectionExtensions.AddLiveCoreRealtime"/>)
/// the SAME group send is published over Redis and also reaches connections on OTHER instances — this class
/// is REUSED UNCHANGED, because scale-out swaps only the transport beneath <c>IHubContext</c>, not this seam
/// or the per-recipient recipient computation. So the anti-leak guarantee (threat T3) is preserved whether or
/// not the backplane is configured.
///
/// It forwards only what it is given — one recipient-safe payload to one server-computed group — so it can
/// never broaden the audience the resolver already authorized ("Events are never broadcast blindly",
/// docs/11_REALTIME_SYNC.md).
/// </summary>
internal sealed class InProcessRealtimeBackplane : IRealtimeBackplane
{
    private readonly IHubContext<SessionHub> _hub;

    public InProcessRealtimeBackplane(IHubContext<SessionHub> hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
    }

    /// <inheritdoc />
    public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(payload);

        return _hub.Clients.Group(group).SendAsync(method, payload, cancellationToken);
    }
}
