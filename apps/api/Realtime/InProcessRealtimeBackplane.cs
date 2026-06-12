using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.Realtime;

/// <summary>
/// The default, single-instance <see cref="IRealtimeBackplane"/> (CORE-RT-006): it delivers a computed
/// delivery to the connections held by THIS API instance by sending to the SignalR group over
/// <see cref="IHubContext{SessionHub}"/> (registered by <c>AddSignalR()</c> — part of the ASP.NET Core
/// shared framework, so no new dependency). This is exactly the send the <c>SessionEventPublisher</c>
/// performed inline before the scale-out seam existed; extracting it behind <see cref="IRealtimeBackplane"/>
/// is what lets a multi-instance deployment swap in a Valkey/Redis-compatible backplane
/// (docs/11_REALTIME_SYNC.md "Scale-out") without touching the per-recipient recipient computation, so the
/// anti-leak guarantee (threat T3) is preserved across the swap.
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
