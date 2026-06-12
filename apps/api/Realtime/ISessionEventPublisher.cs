namespace LiveCore.Api.Realtime;

/// <summary>
/// Appends a session event to the durable stream and delivers it to its recipient groups (CORE-RT-003) —
/// the "persist event -> compute recipients -> ... -> send to recipient groups" steps of the documented
/// delivery flow (docs/11_REALTIME_SYNC.md). This is the single seam a command (for example the reveal
/// command) uses to emit a realtime event, so the Realtime module stays the sole owner of event delivery
/// and "Events are never broadcast blindly" (docs/11_REALTIME_SYNC.md; docs/05_MODULE_CONTRACTS.md: the
/// Realtime module "may not send unfiltered events").
/// </summary>
public interface ISessionEventPublisher
{
    /// <summary>
    /// Appends the event to its session's append-only stream, then delivers its recipient-safe envelope
    /// to the SERVER-COMPUTED recipient groups for the event's routing (a selected participant, or the
    /// audience). The append is the source of truth; delivery is best-effort over the realtime transport.
    /// </summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    Task PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);
}
