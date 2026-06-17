// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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

    /// <summary>
    /// Resolves the recipient deliveries for a BATCH of events (CORE-PERF-002), returning one delivery list
    /// per input event in the SAME order. It is identical in outcome to calling <see cref="ResolveAsync"/>
    /// per event — the SAME routing and the SAME central Visibility gate — but resolves the audience
    /// visibility of the whole batch's distinct subjects with a SINGLE batched lookup
    /// (<see cref="LiveCore.Api.Visibility.IEventRecipientVisibility.ResolveAudienceRecipientsBatchAsync"/>,
    /// CORE-PERF-001 reuse) rather than one query per event, so reconnect replay cost does not grow with the
    /// number of events.
    /// The per-event delivery list is byte-for-byte what <see cref="ResolveAsync"/> would have produced, so
    /// nothing leaks that live delivery would have withheld (threat T3).
    /// </summary>
    /// <exception cref="ArgumentNullException">The event list is null.</exception>
    Task<IReadOnlyList<IReadOnlyList<SessionEventDelivery>>> ResolveBatchAsync(
        IReadOnlyList<SessionEvent> sessionEvents,
        CancellationToken cancellationToken);
}
