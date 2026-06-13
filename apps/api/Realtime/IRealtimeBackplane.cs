namespace LiveCore.Api.Realtime;

/// <summary>
/// The realtime scale-out seam (CORE-RT-006) — the single transport boundary a server-computed event
/// delivery crosses on its way to the connected clients. docs/11_REALTIME_SYNC.md ("Scale-out") mandates a
/// "Valkey/Redis-compatible backplane later when multiple API instances run"; this abstraction is that seam.
/// The <see cref="InProcessRealtimeBackplane"/> fans a delivery out to the SignalR group over the hub
/// (<c>IHubContext&lt;SessionHub&gt;</c>). On a single API instance that reaches only the connections held
/// by THIS instance; a multi-instance deployment configures the SignalR Redis/Valkey HubLifetimeManager
/// (CORE-OPS-007, <see cref="RealtimeServiceCollectionExtensions.AddLiveCoreRealtime"/>) so the SAME group
/// send is published over Redis and ALSO reaches the connections held by OTHER instances — without changing
/// this seam or anything upstream of it (only the transport beneath <c>IHubContext</c> changes).
///
/// CRUCIALLY, the backplane receives an ALREADY-COMPUTED delivery: one recipient-safe payload addressed to
/// exactly ONE server-managed group (<see cref="RealtimeGroups"/>), produced by the per-recipient
/// <c>SessionEventRecipientResolver</c> (CORE-RT-004) and only ever invoked by the
/// <c>SessionEventPublisher</c>. It cannot widen the audience: it has no event, no visibility subject and no
/// way to enumerate recipients — it can only forward what the resolver already authorized. So the recipient
/// resolver stays the single send path and "Realtime delivery never leaks hidden events" (threat T3 in
/// docs/07_SECURITY_THREAT_MODEL.md) holds for every backplane, in-process or scaled-out, by construction
/// (docs/05_MODULE_CONTRACTS.md: the Realtime module "may not send unfiltered events").
///
/// This is a PUBLIC contract so a deployment can substitute its own scale-out transport; the group is an
/// opaque, server-owned routing key (a client never chooses it), and the payload is forwarded verbatim.
/// </summary>
public interface IRealtimeBackplane
{
    /// <summary>
    /// Forwards a single recipient-safe <paramref name="payload"/> to every connection in the
    /// server-managed <paramref name="group"/>, via the client <paramref name="method"/>, across whatever
    /// set of API instances this backplane spans (just this one for the in-process default). The
    /// <paramref name="group"/> is a server-computed name (<see cref="RealtimeGroups"/>), never client
    /// input; the recipient set was already authorized by the per-recipient resolver, so this method must
    /// not re-derive or broaden it.
    /// </summary>
    /// <exception cref="ArgumentException">The group or method is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">The payload is null.</exception>
    Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken);
}
