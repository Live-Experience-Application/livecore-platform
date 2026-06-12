namespace LiveCore.Api.Store;

/// <summary>
/// The persisted record of a verified store purchase (CORE-STORE-002, the purchase transaction persistence and
/// audit trail story of the "Store Purchase Verification" epic). CORE-STORE-001 modeled the provider abstraction
/// that verifies a purchase proof and produces a normalized <see cref="VerifiedPurchase"/>; that story
/// explicitly deferred "persistence of the verified transaction" to here. A <see cref="PurchaseTransaction"/> is
/// that durable row: the server-side source of truth for one purchase, the basis for granting the resulting
/// entitlement (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification" step 4: "Backend
/// persists PurchaseTransaction").
///
/// THE EPIC STORY'S ACCEPTANCE CRITERION — "Purchase state changes are persisted and auditable." The
/// transaction holds the current purchase <see cref="Status"/> (the persisted state), and every change to it is
/// recorded as an append-only <see cref="PurchaseEvent"/> (the audit trail), so a purchase's lifecycle is fully
/// reconstructable from immutable facts. The transaction never trusts a client: it is created only from a
/// <see cref="VerifiedPurchase"/> a provider adapter already verified server-side ("Never trust client-side
/// premium flags"; "Never unlock limits before server verification succeeds", docs/21).
///
/// IDEMPOTENTLY KEYED. A purchase is named idempotently by the pair (<see cref="Provider"/>,
/// <see cref="ProviderTransactionId"/>) — the provider-assigned transaction id is unique within its provider, so
/// the unique <c>purchase_transactions(provider, provider_transaction_id)</c> index makes recording the same
/// verified purchase twice (a client retry, a replayed proof, a duplicate notification) a safe no-op that
/// creates no second row and no duplicate audit event ("Store notifications must be idempotent", docs/21). This
/// is the persistence analogue of how the reveal command keys on an <c>Idempotency-Key</c>.
///
/// NOT TENANT CONTENT, NO BUYER COLUMN. The transaction records only the verified purchase identity and its
/// state. The buyer/subject linkage — which user or workspace the purchase grants premium to — is the separate
/// <c>billing_account_links</c> table of docs/21 (a later story), not a column here, exactly as the documented
/// "Database additions" list <c>purchase_transactions</c> and <c>billing_account_links</c> as distinct tables.
///
/// NO SECRETS. The stored identifiers (provider, the provider transaction id, the product reference) are stable
/// identifiers, not secrets — only the raw verification proof is a secret, and the proof is NEVER persisted (it
/// is consumed by the provider adapter at verification time and excluded from <see cref="VerifiedPurchase"/>),
/// so <see cref="ToString"/> may carry the identifiers for structured logs (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md concerns content/credentials, not identifiers).
/// </summary>
public sealed class PurchaseTransaction
{
    /// <summary>
    /// Maximum stored length of the <see cref="ProviderTransactionId"/> and <see cref="ProductReference"/>
    /// identifiers — the same bound a <see cref="VerifiedPurchase"/> normalizes them to, so a verified purchase
    /// always fits.
    /// </summary>
    public const int MaxIdentifierLength = VerifiedPurchase.MaxIdentifierLength;

    private PurchaseTransaction(
        Guid id,
        PurchaseProvider provider,
        string providerTransactionId,
        string productReference,
        PurchaseTransactionStatus status,
        DateTimeOffset recordedAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Purchase transaction id must not be empty.", nameof(id));
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        ProviderTransactionId = NormalizeIdentifier(providerTransactionId, nameof(providerTransactionId));
        ProductReference = NormalizeIdentifier(productReference, nameof(productReference));

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Status is not a defined purchase transaction status.");
        }

        if (updatedAt < recordedAt)
        {
            throw new ArgumentException(
                "A purchase transaction cannot be updated before it was recorded.",
                nameof(updatedAt));
        }

        Id = id;
        Provider = provider;
        Status = status;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        RecordedAt = recordedAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private PurchaseTransaction()
    {
        // Non-null reference defaults for the EF materialization path; the real values are set from the stored
        // columns.
        ProviderTransactionId = null!;
        ProductReference = null!;
    }

    /// <summary>Surrogate key of the row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>The store that issued and verified this purchase. Part of the idempotency key. Immutable.</summary>
    public PurchaseProvider Provider { get; }

    /// <summary>
    /// The provider-assigned, opaque transaction identifier of the verified purchase (Apple's transaction id /
    /// Google's order or token-derived id). Unique within its <see cref="Provider"/>, so the pair
    /// (<see cref="Provider"/>, <see cref="ProviderTransactionId"/>) idempotently names one purchase. Immutable.
    /// </summary>
    public string ProviderTransactionId { get; }

    /// <summary>The opaque store product/subscription identifier the verified purchase is for. Immutable.</summary>
    public string ProductReference { get; }

    /// <summary>
    /// The current lifecycle state of the purchase (the persisted state). A verified purchase starts
    /// <see cref="PurchaseTransactionStatus.Active"/>; every change is recorded as an append-only
    /// <see cref="PurchaseEvent"/>.
    /// </summary>
    public PurchaseTransactionStatus Status { get; private set; }

    /// <summary>When the verified purchase was first persisted (UTC).</summary>
    public DateTimeOffset RecordedAt { get; }

    /// <summary>When the transaction's status last changed (UTC); equals <see cref="RecordedAt"/> until then.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Records a verified purchase as a new transaction in the <see cref="PurchaseTransactionStatus.Active"/>
    /// state. The identifiers come from the already-verified <see cref="VerifiedPurchase"/> a provider adapter
    /// produced (CORE-STORE-001), so the transaction is never built from untrusted client input. Its initial
    /// state is recorded as a <see cref="PurchaseEvent"/> by the recording service.
    /// </summary>
    /// <param name="purchase">The normalized, verified purchase to persist.</param>
    /// <param name="recordedAt">When the verified purchase is persisted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="purchase"/> is null.</exception>
    public static PurchaseTransaction Record(VerifiedPurchase purchase, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(purchase);

        return new PurchaseTransaction(
            Guid.CreateVersion7(),
            purchase.Provider,
            purchase.ProviderTransactionId,
            purchase.ProductReference,
            PurchaseTransactionStatus.Active,
            recordedAt,
            recordedAt);
    }

    /// <summary>
    /// Changes the purchase's lifecycle state and reports whether it actually changed. Returns
    /// <see langword="true"/> when the status moved (the caller records a <see cref="PurchaseEvent"/> for the
    /// change), or <see langword="false"/> when the new status equals the current one — an IDEMPOTENT no-op that
    /// changes nothing and writes no audit event, so a repeated or replayed state change (a duplicate store
    /// notification) is safe ("Store notifications must be idempotent", docs/21).
    ///
    /// This is the generic, validated state-change primitive: it does not encode which transitions a particular
    /// store notification implies, nor the entitlement effect of a refund or cancellation — that lives in the
    /// later store-notification handler (CORE-STORE-005). It only mutates the persisted state and surfaces the
    /// before/after for the audit trail.
    /// </summary>
    /// <param name="newStatus">The status to move to.</param>
    /// <param name="occurredAt">When the change occurred.</param>
    /// <returns><see langword="true"/> if the status changed; <see langword="false"/> if it was already this status.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The status is not a defined value.</exception>
    public bool ChangeStatus(PurchaseTransactionStatus newStatus, DateTimeOffset occurredAt)
    {
        if (!Enum.IsDefined(newStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(newStatus), newStatus, "Status is not a defined purchase transaction status.");
        }

        if (newStatus == Status)
        {
            return false;
        }

        Status = newStatus;
        UpdatedAt = occurredAt.ToUniversalTime();
        return true;
    }

    private static string NormalizeIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A purchase transaction identifier must be provided.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > MaxIdentifierLength)
        {
            throw new ArgumentException(
                $"A purchase transaction identifier must be at most {MaxIdentifierLength} characters.",
                parameterName);
        }

        return normalized;
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: the row id, provider, provider
    /// transaction id, product reference and status. These are stable identifiers and an enum, never the
    /// verification proof (which is never stored) or any receipt content (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"PurchaseTransaction {Id} provider={Provider} transactionId={ProviderTransactionId} "
            + $"productReference={ProductReference} status={Status}";
}
