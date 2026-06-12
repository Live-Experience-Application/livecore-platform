namespace LiveCore.Api.Store;

/// <summary>
/// The lifecycle state of a persisted <see cref="PurchaseTransaction"/> (CORE-STORE-002, the purchase
/// transaction persistence and audit trail story of the "Store Purchase Verification" epic). A verified
/// purchase is persisted in the <see cref="Active"/> state and later settles into one of the other states as a
/// store reports renewals, cancellations, refunds and grace periods
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification": "Store server notifications update
/// entitlement state on renewals, cancellations, refunds and grace periods"). Every move between these states is
/// a purchase STATE CHANGE that is recorded as an append-only <see cref="PurchaseEvent"/>, so "all purchase
/// state changes must be auditable" (docs/21 "Security requirements"; the epic story's acceptance criterion
/// "Purchase state changes are persisted and auditable").
///
/// This enum is the GENERIC, product-neutral catalog of purchase states; it carries no store-notification
/// parsing and no entitlement effect. WHICH provider notification drives WHICH transition, the idempotent
/// ingestion of those notifications, and the entitlement downgrade/revocation a refund or cancellation causes
/// are the later store-notification story (CORE-STORE-005); this story models only the persisted state and its
/// auditable change.
///
/// Persisted by its stable NAME (the <c>purchase_transactions.status</c> and
/// <c>purchase_events.previous_status</c>/<c>new_status</c> columns use <c>HasConversion&lt;string&gt;</c>, like
/// every other enum in the model — <c>PurchaseProvider</c>, <c>SessionStatus</c>, <c>VisibilityState</c>), so
/// the integers below are only in-memory discriminators with no ordering meaning and must not be compared with
/// &gt;/&lt;.
/// </summary>
public enum PurchaseTransactionStatus
{
    /// <summary>
    /// The purchase is verified and currently in force — the state a verified purchase is first persisted in
    /// (the entitling state a later story grants a <c>SubjectEntitlement</c> from). A renewal keeps a
    /// subscription in this state.
    /// </summary>
    Active = 1,

    /// <summary>
    /// The purchase was cancelled — a subscription will not renew, or a one-off purchase was cancelled. The
    /// entitlement downgrade this causes is the later store-notification story (CORE-STORE-005); here it is the
    /// recorded, auditable state only.
    /// </summary>
    Cancelled = 2,

    /// <summary>
    /// The purchase was refunded or charged back. A refund must revoke or downgrade the entitlement it granted
    /// (docs/21 "Refunds and chargebacks must revoke or downgrade entitlements"); that revocation is the later
    /// store-notification story (CORE-STORE-005), recorded here as the auditable state.
    /// </summary>
    Refunded = 3,

    /// <summary>
    /// The purchase is in a billing-retry grace period (a renewal failed but the entitlement is retained for a
    /// window). Grace periods must be represented EXPLICITLY (docs/21 "Grace periods must be represented
    /// explicitly"), so they are a first-class state rather than an implicit one.
    /// </summary>
    InGracePeriod = 4,
}
