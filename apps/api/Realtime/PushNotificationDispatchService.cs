// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Realtime;

/// <summary>
/// The OUTBOUND half of the closed-app push fan-out (CORE-PUSH-002): the worker's dispatch sweep. On the worker's
/// configurable cadence it drains the push OUTBOX (<see cref="IPushNotificationDeliveryRepository"/>) — the rows the
/// API's publish-time fan-out enqueued for the already-authorized, recipient-filtered audience — and sends a
/// CONTENT-FREE push to each recipient's registered subscriptions through the <see cref="IWebPushSender"/> seam. The
/// scheduling host (the worker) invokes this on an interval (docs/02_ARCHITECTURE.md: the worker owns async jobs),
/// exactly as it invokes the scheduled-reveal and recap-generation services; the service itself is host-agnostic and
/// fully unit-testable.
///
/// <para>
/// THE 410 CLEANUP (the acceptance criterion). A push endpoint that reports the subscription GONE (HTTP 404/410)
/// causes the stale subscription to be DELETED (<see cref="IPushSubscriptionRepository.DeleteByIdForUserAsync"/>,
/// scoped to its owner), so a dead browser endpoint is reclaimed rather than retried forever.
/// </para>
///
/// <para>
/// BEST-EFFORT AND CONTENT-FREE. Each row is processed and then DELETED whatever the per-subscription outcome — the
/// push is best-effort, exactly like the realtime transport, and a missed signal is recovered when the client next
/// opens the app and replays (CORE-RT-005). Only IDENTIFIERS are sent and logged, never any projected content
/// (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md). The sweep is per-row RESILIENT: a transient failure on one
/// row is logged (identifiers/counts only) and the sweep continues, never aborting the whole run.
/// </para>
/// </summary>
public sealed class PushNotificationDispatchService
{
    private readonly IPushNotificationDeliveryRepository _deliveries;
    private readonly IPushSubscriptionRepository _subscriptions;
    private readonly IWebPushSender _sender;
    private readonly WebPushDeliveryOptions _options;
    private readonly ILogger<PushNotificationDispatchService> _logger;

    public PushNotificationDispatchService(
        IPushNotificationDeliveryRepository deliveries,
        IPushSubscriptionRepository subscriptions,
        IWebPushSender sender,
        WebPushDeliveryOptions options,
        ILogger<PushNotificationDispatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _deliveries = deliveries;
        _subscriptions = subscriptions;
        _sender = sender;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Runs one push dispatch sweep: drains up to <see cref="WebPushDeliveryOptions.BatchSize"/> pending outbox
    /// rows, sends a content-free push to each recipient's registered subscriptions, deletes any subscription a push
    /// service reports gone (404/410), and removes each processed row. Returns the run's count-only summary. The
    /// sweep is per-row resilient and best-effort.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <returns>A summary of how many rows were examined, pushes delivered, subscriptions removed and sends failed.</returns>
    public async Task<PushNotificationDispatchResult> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await _deliveries
            .ListPendingAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return new PushNotificationDispatchResult(Examined: 0, Delivered: 0, SubscriptionsRemoved: 0, Failed: 0);
        }

        var examined = 0;
        var delivered = 0;
        var removed = 0;
        var failed = 0;

        foreach (var delivery in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            try
            {
                var outcome = await DispatchOneAsync(delivery, cancellationToken).ConfigureAwait(false);
                delivered += outcome.Delivered;
                removed += outcome.SubscriptionsRemoved;
                failed += outcome.Failed;

                // Remove the processed row whatever the per-subscription outcome (best-effort drain).
                await _deliveries.DeleteAsync(delivery, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown during the sweep is expected; stop cleanly and let the loop end.
                throw;
            }
            catch (DbUpdateException exception)
            {
                // A persistence error on this row (e.g. it was cascade-removed between the read and the delete):
                // log identifiers/counts only (threat T7) and continue rather than aborting the run.
                failed++;
                _logger.LogWarning(
                    exception,
                    "Failed to process push delivery {DeliveryId} for recipient {RecipientId}; it is left for the next sweep.",
                    delivery.Id,
                    delivery.RecipientUserProfileId);
            }
        }

        return new PushNotificationDispatchResult(
            Examined: examined, Delivered: delivered, SubscriptionsRemoved: removed, Failed: failed);
    }

    /// <summary>
    /// Dispatches ONE outbox row: resolves the recipient's current subscriptions and sends the content-free signal
    /// to each, deleting any the push service reports gone (404/410). Returns the per-row counts.
    /// </summary>
    private async Task<(int Delivered, int SubscriptionsRemoved, int Failed)> DispatchOneAsync(
        PushNotificationDelivery delivery,
        CancellationToken cancellationToken)
    {
        // Resolve the recipient's CURRENT subscriptions at dispatch time, so a subscription deleted between enqueue
        // and dispatch is simply not sent to (and a recipient who has since unsubscribed gets nothing).
        var subscriptions = await _subscriptions
            .ListByUserAsync(delivery.RecipientUserProfileId, cancellationToken)
            .ConfigureAwait(false);

        var delivered = 0;
        var removed = 0;
        var failed = 0;

        foreach (var subscription in subscriptions)
        {
            // Content-free signal: identifiers only (the source event + session), never projected content (T2/T7).
            var dispatch = new WebPushDispatch(
                subscription.Endpoint,
                subscription.P256dh,
                subscription.Auth,
                new WebPushSignal(delivery.SessionEventId, delivery.SessionId));

            var result = await _sender.SendAsync(dispatch, cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case WebPushSendOutcome.Delivered:
                    delivered++;
                    break;

                case WebPushSendOutcome.Gone:
                    // THE 410 CLEANUP: delete the stale subscription (scoped to its owner, fail-closed).
                    removed++;
                    await _subscriptions
                        .DeleteByIdForUserAsync(subscription.Id, subscription.UserProfileId, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogInformation(
                        "Deleted a gone push subscription {SubscriptionId} for recipient {RecipientId} after a 404/410.",
                        subscription.Id,
                        subscription.UserProfileId);
                    break;

                case WebPushSendOutcome.Failed:
                default:
                    // Best-effort: a transient send failure is counted and dropped (the client recovers on replay).
                    failed++;
                    break;
            }
        }

        return (delivered, removed, failed);
    }
}

/// <summary>The count-only summary of one push dispatch sweep (CORE-PUSH-002); carries no tenant/content detail (T7).</summary>
/// <param name="Examined">How many pending outbox rows the sweep drained.</param>
/// <param name="Delivered">How many pushes a push service accepted.</param>
/// <param name="SubscriptionsRemoved">How many stale subscriptions were deleted after a 404/410.</param>
/// <param name="Failed">How many sends failed (best-effort, dropped).</param>
public readonly record struct PushNotificationDispatchResult(
    int Examined,
    int Delivered,
    int SubscriptionsRemoved,
    int Failed);
