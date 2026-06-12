namespace LiveCore.Api.Store;

/// <summary>
/// Records verified purchases and their state changes with an append-only audit trail (CORE-STORE-002, the
/// purchase transaction persistence and audit trail story of the "Store Purchase Verification" epic). It is the
/// application service that turns a CORE-STORE-001 <see cref="VerifiedPurchase"/> into a persisted
/// <see cref="PurchaseTransaction"/> and writes a <see cref="PurchaseEvent"/> for every purchase state change —
/// the acceptance criterion "Purchase state changes are persisted and auditable" (docs/21 "All purchase state
/// changes must be auditable").
///
/// IDEMPOTENT. Recording the same verified purchase twice — a client retry, a replayed proof, or a duplicate
/// store notification — persists exactly one transaction and writes exactly one initial audit event; the repeat
/// is recognized (by the (provider, provider transaction id) idempotency key) and returns
/// <see cref="PurchaseRecordingOutcome.AlreadyRecorded"/> without any duplicate effect ("Store notifications
/// must be idempotent", docs/21). A status change to the status a transaction is already in is likewise a no-op
/// that writes no event.
///
/// AUTHORIZATION IS UPSTREAM. This service performs the persistence and audit mechanism only; it does not
/// authorize the caller or verify a proof. The verification endpoints that TRIGGER a recording (the later Apple
/// CORE-STORE-003 and Google CORE-STORE-004 routes) authorize the caller server-side and resolve the adapter to
/// verify the proof, then hand the resulting <see cref="VerifiedPurchase"/> here; the store-notification handler
/// that TRIGGERS a status change (the later CORE-STORE-005) ingests provider notifications idempotently and maps
/// them to the status change and the resulting entitlement effect. This story supplies the generic, reusable
/// persistence + audit primitives they build on (the same way the entitlement assignment service supplied the
/// reusable grant/revoke primitives).
/// </summary>
public sealed class PurchaseTransactionService
{
    private readonly IPurchaseTransactionRepository _transactions;
    private readonly IPurchaseEventRepository _events;

    public PurchaseTransactionService(
        IPurchaseTransactionRepository transactions,
        IPurchaseEventRepository events)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(events);
        _transactions = transactions;
        _events = events;
    }

    /// <summary>
    /// Records a verified purchase as a persisted transaction and audits its initial state. If the purchase was
    /// already recorded (same provider + provider transaction id), it returns the existing transaction as
    /// <see cref="PurchaseRecordingOutcome.AlreadyRecorded"/> and writes nothing further (idempotent). Otherwise
    /// it persists a new <see cref="PurchaseTransactionStatus.Active"/> transaction, appends the initial
    /// <see cref="PurchaseEvent"/> and returns <see cref="PurchaseRecordingOutcome.Recorded"/>.
    /// </summary>
    /// <param name="purchase">The normalized, verified purchase to record.</param>
    /// <param name="recordedAt">When the purchase is recorded.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="purchase"/> is null.</exception>
    public async Task<PurchaseRecordingResult> RecordVerifiedPurchaseAsync(
        VerifiedPurchase purchase,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(purchase);

        // Idempotency: a purchase already recorded is returned as-is, with no second row and no duplicate audit
        // event.
        var existing = await _transactions
            .FindByProviderTransactionAsync(purchase.Provider, purchase.ProviderTransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new PurchaseRecordingResult(PurchaseRecordingOutcome.AlreadyRecorded, existing);
        }

        var transaction = PurchaseTransaction.Record(purchase, recordedAt);
        var addResult = await _transactions.AddAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (addResult == PurchaseTransactionAddResult.DuplicateTransaction)
        {
            // A concurrent recording won the race between the find and the insert; re-read its row so the caller
            // still gets the canonical transaction (idempotent), and write no duplicate event.
            var raced = await _transactions
                .FindByProviderTransactionAsync(purchase.Provider, purchase.ProviderTransactionId, cancellationToken)
                .ConfigureAwait(false);
            return new PurchaseRecordingResult(
                PurchaseRecordingOutcome.AlreadyRecorded,
                raced ?? transaction);
        }

        // Audit the initial state. The transaction row exists (just persisted), so the cascade foreign key is
        // satisfied.
        await _events
            .AppendAsync(PurchaseEvent.ForRecorded(transaction.Id, recordedAt), cancellationToken)
            .ConfigureAwait(false);

        return new PurchaseRecordingResult(PurchaseRecordingOutcome.Recorded, transaction);
    }

    /// <summary>
    /// Changes the lifecycle status of an already-recorded purchase and audits the change. Looks the transaction
    /// up by its (provider, provider transaction id) idempotency key: an unknown purchase is
    /// <see cref="PurchaseStatusChangeOutcome.TransactionNotFound"/> (fail-closed, nothing changes). A change to
    /// the status the transaction is already in is <see cref="PurchaseStatusChangeOutcome.Unchanged"/> — an
    /// idempotent no-op that writes no audit event (so a duplicate notification is safe). A real change persists
    /// the new status and appends a <see cref="PurchaseEvent"/> capturing the before/after pair, returning
    /// <see cref="PurchaseStatusChangeOutcome.Changed"/>.
    /// </summary>
    /// <param name="provider">The store that issued the purchase.</param>
    /// <param name="providerTransactionId">The provider-assigned transaction id naming the purchase.</param>
    /// <param name="newStatus">The status to move to.</param>
    /// <param name="occurredAt">When the change occurred.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The provider transaction id is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The status is not a defined value.</exception>
    public async Task<PurchaseStatusChangeOutcome> ChangeStatusAsync(
        PurchaseProvider provider,
        string providerTransactionId,
        PurchaseTransactionStatus newStatus,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(newStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(newStatus), newStatus, "Status is not a defined purchase transaction status.");
        }

        var transaction = await _transactions
            .FindByProviderTransactionAsync(provider, providerTransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (transaction is null)
        {
            return PurchaseStatusChangeOutcome.TransactionNotFound;
        }

        var previousStatus = transaction.Status;
        if (!transaction.ChangeStatus(newStatus, occurredAt))
        {
            return PurchaseStatusChangeOutcome.Unchanged;
        }

        await _transactions.UpdateAsync(transaction, cancellationToken).ConfigureAwait(false);
        await _events
            .AppendAsync(
                PurchaseEvent.ForStatusChange(transaction.Id, previousStatus, newStatus, occurredAt),
                cancellationToken)
            .ConfigureAwait(false);

        return PurchaseStatusChangeOutcome.Changed;
    }
}
