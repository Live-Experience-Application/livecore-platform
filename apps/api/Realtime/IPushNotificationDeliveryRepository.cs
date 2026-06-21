// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// Persistence contract for the closed-app push OUTBOX (CORE-PUSH-002). The Realtime module owns the
/// <c>push_notification_deliveries</c> table; the publish-time fan-out (<see cref="ISessionEventPushFanOut"/>)
/// enqueues rows and the worker's dispatch service (<see cref="PushNotificationDispatchService"/>) drains them.
///
/// The outbox is a transient hand-off, never a content store: the rows carry only identifiers (threats T2/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md), and the worker deletes each row once it has attempted delivery.
/// </summary>
public interface IPushNotificationDeliveryRepository
{
    /// <summary>
    /// Persists a batch of pending content-free push deliveries (one per resolved recipient of a session event).
    /// A no-op when the batch is empty.
    /// </summary>
    /// <exception cref="ArgumentNullException">The batch is null.</exception>
    Task AddRangeAsync(IReadOnlyCollection<PushNotificationDelivery> deliveries, CancellationToken cancellationToken);

    /// <summary>
    /// Lists up to <paramref name="batchSize"/> pending deliveries in FIFO enqueue order (by the time-ordered
    /// surrogate id), so the worker drains the oldest signals first and one sweep can never do unbounded work
    /// (threat T9). The read is NOT tenant-scoped: the worker drains every tenant's outbox, exactly like the
    /// recap/export/retention jobs sweep cross-tenant; the rows it returns carry their own tenant.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The batch size is not positive.</exception>
    Task<IReadOnlyList<PushNotificationDelivery>> ListPendingAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a processed outbox row. The delivery is best-effort (like the realtime transport), so the row is
    /// removed once the worker has attempted to send it; a missed signal is recovered when the client next opens
    /// the app and replays (CORE-RT-005). Deleting an already-removed row is a no-op.
    /// </summary>
    /// <exception cref="ArgumentNullException">The delivery is null.</exception>
    Task DeleteAsync(PushNotificationDelivery delivery, CancellationToken cancellationToken);
}
