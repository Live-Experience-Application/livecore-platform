// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Persistence contract for the append-only purchase event trail (CORE-STORE-002). The Store module owns the
/// <c>purchase_events</c> table; other modules write or read purchase events only through this contract or the
/// module's application services (docs/02_ARCHITECTURE.md: module boundaries).
///
/// APPEND-ONLY. The contract intentionally exposes only an append and a per-transaction read: there is NO update
/// and NO delete, because a recorded purchase state change is an immutable fact (mirrors
/// <c>IAuditLogRepository</c>; docs/10_DATABASE_SCHEMA.md: "audit logs are append-only"). The read is scoped by
/// the owning transaction id, so it returns exactly one transaction's trail.
/// </summary>
public interface IPurchaseEventRepository
{
    /// <summary>
    /// Appends a purchase event. The event is immutable; this is the only write path. The surrogate id is
    /// generated non-empty by the aggregate, so an insert never collides; a foreign-key violation (a
    /// non-existent transaction) surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task AppendAsync(PurchaseEvent purchaseEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every event of the given transaction, in deterministic chronological order (by the time-ordered
    /// surrogate id, UUIDv7). Scoped by <c>purchase_transaction_id</c>, so another transaction's events are never
    /// returned.
    /// </summary>
    /// <exception cref="System.ArgumentException">The transaction id is empty.</exception>
    Task<IReadOnlyList<PurchaseEvent>> ListByTransactionAsync(
        Guid purchaseTransactionId,
        CancellationToken cancellationToken);
}
