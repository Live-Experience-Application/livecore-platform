namespace LiveCore.Api.Store;

/// <summary>
/// An append-only audit-trail entry for a <see cref="PurchaseTransaction"/> (CORE-STORE-002). A
/// <see cref="PurchaseEvent"/> is the immutable record of ONE purchase state change: a purchase being first
/// recorded, or its <see cref="PurchaseTransactionStatus"/> moving (a renewal, cancellation, refund or grace
/// period). Together the events of a transaction are its full, reconstructable history — the "audit trail" half
/// of the story's acceptance criterion "Purchase state changes are persisted and auditable" and the docs/21
/// security requirement "All purchase state changes must be auditable"
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). It is the Store module's <c>purchase_events</c> table
/// (one of the module's documented "Database additions").
///
/// APPEND-ONLY (mirrors the audit log and the session event stream, docs/10_DATABASE_SCHEMA.md: "audit logs are
/// append-only"). The aggregate is fully immutable — every property is get-only, there is no state-transition
/// method and the repository exposes no update or delete. A purchase event, once written, is never altered, so
/// the trail is trustworthy as a record of how a purchase's state evolved.
///
/// BEFORE/AFTER STATE PAIR. Each event captures the transaction it belongs to
/// (<see cref="PurchaseTransactionId"/>), the <see cref="PreviousStatus"/> (<see langword="null"/> for the
/// initial recording — a purchase has no prior state when first persisted) and the resulting
/// <see cref="NewStatus"/>, exactly as an <c>audit_logs</c> entry records a previous/new state pair. The
/// statuses are persisted by name (a real string column per enum, never JSON — docs/10: JSONB is "not for core
/// authorization fields").
///
/// NO SECRETS, NO CONTENT. An event stores only the transaction id, the status names and the timestamp — never
/// the verification proof (never stored anywhere) and never receipt content — so <see cref="ToString"/> is safe
/// for structured logs (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class PurchaseEvent
{
    private PurchaseEvent(
        Guid id,
        Guid purchaseTransactionId,
        PurchaseTransactionStatus? previousStatus,
        PurchaseTransactionStatus newStatus,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Purchase event id must not be empty.", nameof(id));
        }

        if (purchaseTransactionId == Guid.Empty)
        {
            throw new ArgumentException("Purchase transaction id must not be empty.", nameof(purchaseTransactionId));
        }

        if (previousStatus is { } previous && !Enum.IsDefined(previous))
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousStatus),
                previousStatus,
                "Previous status is not a defined purchase transaction status.");
        }

        if (!Enum.IsDefined(newStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(newStatus), newStatus, "New status is not a defined purchase transaction status.");
        }

        // A recorded event is a real state CHANGE: the resulting status must differ from the prior one. The
        // initial recording (previous = null) trivially differs. This mirrors PurchaseTransaction.ChangeStatus,
        // which never raises an event for a no-op transition.
        if (previousStatus == newStatus)
        {
            throw new ArgumentException(
                "A purchase event must record a state change; the new status must differ from the previous status.",
                nameof(newStatus));
        }

        Id = id;
        PurchaseTransactionId = purchaseTransactionId;
        PreviousStatus = previousStatus;
        NewStatus = newStatus;
        // Normalized to UTC so the persisted timestamptz value is offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private PurchaseEvent()
    {
    }

    /// <summary>Surrogate key of the row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>
    /// The <see cref="PurchaseTransaction"/> this event belongs to (the <c>purchase_transaction_id</c> foreign
    /// key into <c>purchase_transactions</c>, CASCADE on delete — the trail is part of the transaction's
    /// history).
    /// </summary>
    public Guid PurchaseTransactionId { get; }

    /// <summary>
    /// The status BEFORE this change, or <see langword="null"/> for the initial recording (a purchase has no
    /// prior state when first persisted).
    /// </summary>
    public PurchaseTransactionStatus? PreviousStatus { get; }

    /// <summary>The status AFTER this change — always set, and always different from <see cref="PreviousStatus"/>.</summary>
    public PurchaseTransactionStatus NewStatus { get; }

    /// <summary>When the recorded change occurred (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Records the initial persistence of a verified purchase: the first audit event, with no previous status
    /// and a resulting status of <see cref="PurchaseTransactionStatus.Active"/>.
    /// </summary>
    /// <param name="purchaseTransactionId">The transaction that was recorded (non-empty).</param>
    /// <param name="createdAt">When the purchase was recorded.</param>
    /// <exception cref="ArgumentException">The transaction id is empty.</exception>
    public static PurchaseEvent ForRecorded(Guid purchaseTransactionId, DateTimeOffset createdAt)
        => new(Guid.CreateVersion7(), purchaseTransactionId, previousStatus: null, PurchaseTransactionStatus.Active, createdAt);

    /// <summary>
    /// Records a status change of an existing transaction: the previous status and the (different) resulting
    /// status. The before/after pair is the auditable fact of how the purchase's state moved.
    /// </summary>
    /// <param name="purchaseTransactionId">The transaction whose status changed (non-empty).</param>
    /// <param name="previousStatus">The status before the change.</param>
    /// <param name="newStatus">The status after the change (must differ from <paramref name="previousStatus"/>).</param>
    /// <param name="createdAt">When the change occurred.</param>
    /// <exception cref="ArgumentException">The transaction id is empty, or the new status equals the previous status.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A status is not a defined value.</exception>
    public static PurchaseEvent ForStatusChange(
        Guid purchaseTransactionId,
        PurchaseTransactionStatus previousStatus,
        PurchaseTransactionStatus newStatus,
        DateTimeOffset createdAt)
        => new(Guid.CreateVersion7(), purchaseTransactionId, previousStatus, newStatus, createdAt);

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: the event id, the transaction id and the
    /// before/after status names. Every field is an identifier or an enum name, never the verification proof or
    /// any receipt content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"PurchaseEvent {Id} transaction={PurchaseTransactionId} "
            + $"status={(PreviousStatus is { } previous ? previous.ToString() : "none")}->{NewStatus}";
}
