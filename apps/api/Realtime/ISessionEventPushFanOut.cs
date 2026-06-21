// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// Fans an already-delivered session event out to the closed-app push OUTBOX (CORE-PUSH-002). The
/// <see cref="SessionEventPublisher"/> invokes it from the commit-then-publish seam — AFTER the unit of work
/// commits and AFTER the in-session realtime delivery — so the durable event is the source of truth and a push is
/// only ever a follow-on signal. It is INERT unless the deployment has opted in and configured VAPID
/// (<see cref="WebPushDeliveryOptions.IsActive"/>).
///
/// The publisher calls this best-effort and SWALLOWS any failure (recording it on the event-delivery-failure
/// signal), so a push enqueue failure can never block or fail the in-session realtime delivery (the story's
/// requirement).
/// </summary>
internal interface ISessionEventPushFanOut
{
    /// <summary>
    /// Resolves the event's recipient users (the SAME audience the in-app event reached), filters to those with a
    /// registered push subscription, and enqueues one CONTENT-FREE outbox row per such recipient. A no-op when push
    /// delivery is inert, when the event reaches no participant audience, or when no recipient has a subscription.
    /// </summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    Task FanOutAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);
}
