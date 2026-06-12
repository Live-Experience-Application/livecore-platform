namespace LiveCore.Api.Realtime;

/// <summary>
/// Computes the per-recipient deliveries of a session event (CORE-RT-004, "recipient-specific event
/// projection") — the "compute recipients -> project payload" steps of the documented delivery flow
/// (docs/11_REALTIME_SYNC.md). Given a stored <see cref="SessionEvent"/>, it returns the list of
/// <see cref="SessionEventDelivery"/> — each a SERVER-MANAGED group plus the projection that group's
/// recipients may see — so the <see cref="SessionEventPublisher"/> only has to send them. This is the
/// single place "Events are never broadcast blindly" is enforced for delivery (docs/11_REALTIME_SYNC.md;
/// docs/05_MODULE_CONTRACTS.md: the Realtime module "may not send unfiltered events").
/// </summary>
internal interface ISessionEventRecipientResolver
{
    /// <summary>
    /// Resolves the recipient deliveries for the given event: which server-managed groups receive it and
    /// the recipient-safe envelope each gets. Recipients that may not see the event's visibility subject
    /// are omitted, so the delivery never leaks a hidden event (threat T3).
    /// </summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    Task<IReadOnlyList<SessionEventDelivery>> ResolveAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken);
}
