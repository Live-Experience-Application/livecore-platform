// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Outcome of persisting a new <see cref="StoreNotificationEvent"/> (CORE-STORE-005).
///
/// A handled notification is named idempotently by the (provider, provider notification id) pair (the unique
/// <c>store_notification_events(provider, provider_notification_id)</c> index), so an insert can race or repeat an
/// already-handled notification; this enum mirrors <see cref="PurchaseTransactionAddResult"/>: a success value
/// and an explicit duplicate outcome. The handler treats the duplicate outcome as "already processed"
/// (idempotent), so a re-delivered notification never records a second row. Any other failure surfaces as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as a result value.
/// </summary>
public enum StoreNotificationEventAddResult
{
    /// <summary>The store notification event was persisted.</summary>
    Added = 1,

    /// <summary>An event for the same (provider, provider notification id) already exists; the insert was rejected.</summary>
    DuplicateNotification = 2,
}
