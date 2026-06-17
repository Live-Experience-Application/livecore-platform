namespace LiveCore.Api.Realtime;

/// <summary>
/// Appends a session event to the durable stream and delivers it to its recipient groups (CORE-RT-003) —
/// the "persist event -> compute recipients -> ... -> send to recipient groups" steps of the documented
/// delivery flow (docs/11_REALTIME_SYNC.md). This is the single seam a command (for example the reveal
/// command) uses to emit a realtime event, so the Realtime module stays the sole owner of event delivery
/// and "Events are never broadcast blindly" (docs/11_REALTIME_SYNC.md; docs/05_MODULE_CONTRACTS.md: the
/// Realtime module "may not send unfiltered events").
///
/// <para>
/// The two halves — the durable APPEND and the realtime DELIVERY — are exposed separately
/// (<see cref="AppendAsync"/> and <see cref="DeliverAsync"/>) so a multi-step command can place the append
/// INSIDE its unit-of-work transaction (committed atomically with the rule/state change, audit and quota
/// change it accompanies) and run the delivery AFTER the commit (commit-then-publish, CORE-CONC-002), so a
/// delivery failure can never roll back already-committed state. <see cref="PublishAsync"/> remains the
/// combined append-then-deliver for callers that do not need that separation.
/// </para>
/// </summary>
public interface ISessionEventPublisher
{
    /// <summary>
    /// Appends the event to its session's append-only stream, then delivers its recipient-safe envelope
    /// to the SERVER-COMPUTED recipient groups for the event's routing (a selected participant, or the
    /// audience). The append is the source of truth; delivery is best-effort over the realtime transport.
    /// Equivalent to <see cref="AppendAsync"/> followed by <see cref="DeliverAsync"/>, for callers that do
    /// not separate the two across a transaction boundary.
    /// </summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    Task PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the event to its session's append-only stream ONLY (the durable persist, the source of truth
    /// for replay) — it does NOT deliver. A transactional command calls this inside its unit-of-work
    /// transaction so the durable event commits ATOMICALLY with the rule/state change, audit and quota
    /// change it accompanies, then delivers it with <see cref="DeliverAsync"/> after the commit
    /// (CORE-CONC-002).
    /// </summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Delivers an ALREADY-APPENDED event's recipient-safe envelope to the SERVER-COMPUTED recipient groups
    /// ONLY (the realtime transport) — it does NOT append. A transactional command calls this AFTER its
    /// transaction commits (commit-then-publish, CORE-CONC-002), so a delivery failure cannot roll back the
    /// committed state. Delivery is GENUINELY best-effort (CORE-RES-001): a backplane (Redis/Valkey) transport
    /// failure is RECORDED on the "event delivery failures" metric and SWALLOWED, never rethrown — so a
    /// committed reveal/hide/session/participant operation still returns success during a backplane outage
    /// instead of surfacing a <c>500</c>, and a reconnecting client replays the missed push from the durable
    /// stream later (CORE-RT-005).
    /// </summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    Task DeliverAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);
}
