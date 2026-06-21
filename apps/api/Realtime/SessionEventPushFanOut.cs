// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.Realtime;

/// <summary>
/// The publish-time half of the closed-app push fan-out (CORE-PUSH-002, <see cref="ISessionEventPushFanOut"/>). It
/// runs in the API's commit-then-publish seam (<see cref="SessionEventPublisher.DeliverAsync"/>): the durable event
/// has committed and the in-session realtime delivery has run, so this records the CONTENT-FREE push the event's
/// recipients are owed and hands the slow outbound HTTP send to the worker (the outbox pattern, so a failed/slow
/// push never blocks realtime delivery).
///
/// <para>
/// IT RESOLVES THE RECIPIENTS AT PUBLISH TIME, reusing the central recipient resolution
/// (<see cref="ISessionEventPushRecipientResolver"/>, gated through the central Visibility engine), so the enqueued
/// audience is EXACTLY the audience the in-app event reached — captured now, never recomputed later (so it cannot
/// drift), and never wider than the authorized set (threats T2/T3 in docs/07_SECURITY_THREAT_MODEL.md). It then
/// filters to recipients that actually have a registered subscription ("to each recipient WITH a registered
/// subscription") and writes one identifier-only outbox row each.
/// </para>
///
/// <para>
/// INERT BY DEFAULT. When the deployment has not opted in / not configured VAPID
/// (<see cref="WebPushDeliveryOptions.IsActive"/> is false) it returns immediately, doing NO database work — so an
/// unconfigured deployment attempts no push (the acceptance criterion). It composes only identifiers; no projected
/// content is ever read or stored (threat T7).
/// </para>
/// </summary>
internal sealed class SessionEventPushFanOut : ISessionEventPushFanOut
{
    private readonly WebPushDeliveryOptions _options;
    private readonly ISessionEventPushRecipientResolver _recipients;
    private readonly IPushSubscriptionRepository _subscriptions;
    private readonly IPushNotificationDeliveryRepository _deliveries;
    private readonly TimeProvider _timeProvider;

    public SessionEventPushFanOut(
        WebPushDeliveryOptions options,
        ISessionEventPushRecipientResolver recipients,
        IPushSubscriptionRepository subscriptions,
        IPushNotificationDeliveryRepository deliveries,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options;
        _recipients = recipients;
        _subscriptions = subscriptions;
        _deliveries = deliveries;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task FanOutAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // INERT unless the deployment opted in and configured VAPID. No recipient resolution, no database work.
        if (!_options.IsActive)
        {
            return;
        }

        // EXACTLY the audience the in-app event reached, resolved through the central recipient resolution / the
        // central Visibility engine (never recomputed, never wider; threats T2/T3).
        var recipientUserIds = await _recipients
            .ResolveRecipientUserIdsAsync(sessionEvent, cancellationToken)
            .ConfigureAwait(false);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        // Keep only the recipients that actually have a registered subscription, so the outbox holds exactly "each
        // recipient WITH a registered subscription" — never a row for an unsubscribed recipient.
        var subscribedUserIds = await _subscriptions
            .ListUserIdsWithSubscriptionsAsync(recipientUserIds, cancellationToken)
            .ConfigureAwait(false);
        if (subscribedUserIds.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var rows = new List<PushNotificationDelivery>(subscribedUserIds.Count);
        foreach (var recipientUserId in subscribedUserIds)
        {
            rows.Add(PushNotificationDelivery.Create(
                sessionEvent.OrganizationId,
                sessionEvent.SessionId,
                sessionEvent.Id,
                recipientUserId,
                now));
        }

        await _deliveries.AddRangeAsync(rows, cancellationToken).ConfigureAwait(false);
    }
}
