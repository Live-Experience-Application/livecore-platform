// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.Store;

/// <summary>
/// The durable link from a verified <see cref="PurchaseTransaction"/> to the authenticated BUYER (a generic
/// subject) — the Store module's <c>billing_account_links</c> table (CORE-MON-002, the buyer-linkage story of
/// the "Monetization v1" epic; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md and docs/24_SPEC_CONSISTENCY.md
/// list it as a distinct "Database addition"). CORE-STORE-002 deliberately gave <see cref="PurchaseTransaction"/>
/// NO buyer column — a purchase is named GLOBALLY by its (provider, provider transaction id) pair, so the
/// authenticated buyer was verified and then discarded. This is the missing link that records WHICH subject a
/// verified purchase belongs to, so a later story (CORE-MON-003) can grant that subject the mapped
/// <see cref="SubjectEntitlement"/>.
///
/// THE STORY ACCEPTANCE CRITERION — "A verified purchase is durably linked to the authenticated buyer (subject)
/// so it can later grant that subject an entitlement; the same external receipt cannot be claimed by two
/// different subjects." A link binds ONE purchase to ONE subject. The receipt (the external purchase, collapsed
/// to one <c>purchase_transactions</c> row by its idempotency key) maps to exactly one subject: the unique
/// <c>billing_account_links(purchase_transaction_id)</c> index makes a purchase linkable to only one subject, so
/// once user A's receipt is linked to A, user B can never bind the same receipt to B (fail-closed — the binding
/// is denied, the receipt stays A's).
///
/// THE SUBJECT. The buyer is recorded as a generic subject pair (<see cref="SubjectType"/>,
/// <see cref="SubjectId"/>) — the SAME shape <see cref="SubjectEntitlement"/> keys a premium-state assignment on
/// — so the buyer-to-entitlement grant chain reads the subject directly. A store purchase is made by a person,
/// so the buyer is always an <see cref="EntitlementSubjectType.User"/> subject whose id is the buyer's
/// <c>users(id)</c> profile id; the pair is kept generic to align with the entitlement model. The subject id is
/// a POLYMORPHIC reference with NO database foreign key (mirrors <c>subject_entitlements.subject_id</c>,
/// <c>visibility_rules.resource_id</c> and <c>asset_links.target_id</c>), so isolation is purely by the subject
/// pair — one subject's purchase is never read or claimed through another subject's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// IMMUTABLE. A link is created once and never updated: which subject a verified purchase belongs to does not
/// change (re-binding the receipt to a different subject is exactly what the uniqueness rule forbids). The
/// aggregate therefore exposes no mutator and the repository no update — like the append-only
/// <see cref="PurchaseEvent"/>, it carries no optimistic-concurrency token. The link is removed only when its
/// purchase row is (the <c>purchase_transaction_id</c> foreign key cascades).
///
/// NO SECRETS, NO CONTENT. A row stores only identifiers (the row id, the linked purchase id and the subject
/// pair) — never the verification proof (never persisted anywhere) and never receipt content — so
/// <see cref="ToString"/> is safe for structured logs (threat T7 concerns content/credentials, not identifiers).
/// </summary>
public sealed class BillingAccountLink
{
    private BillingAccountLink(
        Guid id,
        Guid purchaseTransactionId,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        DateTimeOffset linkedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Billing account link id must not be empty.", nameof(id));
        }

        if (purchaseTransactionId == Guid.Empty)
        {
            throw new ArgumentException("Purchase transaction id must not be empty.", nameof(purchaseTransactionId));
        }

        if (!Enum.IsDefined(subjectType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(subjectType),
                subjectType,
                "Subject type is not a defined entitlement subject type.");
        }

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        Id = id;
        PurchaseTransactionId = purchaseTransactionId;
        SubjectType = subjectType;
        SubjectId = subjectId;
        // Normalized to UTC so the persisted timestamptz value is offset-independent (docs/10_DATABASE_SCHEMA.md).
        LinkedAt = linkedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private BillingAccountLink()
    {
    }

    /// <summary>Surrogate key of the row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>
    /// The verified purchase this link binds to a subject (the <c>purchase_transaction_id</c> foreign key into
    /// <c>purchase_transactions(id)</c>, CASCADE on delete — the link is part of the purchase's lifecycle). The
    /// UNIQUE index on this column is the "one subject per receipt" guarantee. Immutable.
    /// </summary>
    public Guid PurchaseTransactionId { get; }

    /// <summary>The kind of subject the purchase is linked to (a user or a workspace). Immutable.</summary>
    public EntitlementSubjectType SubjectType { get; }

    /// <summary>
    /// The id of the buyer subject (a <c>users(id)</c> profile id for a user subject). A generic POLYMORPHIC
    /// reference (no database foreign key), so isolation is by the (<see cref="SubjectType"/>,
    /// <see cref="SubjectId"/>) pair. Immutable.
    /// </summary>
    public Guid SubjectId { get; }

    /// <summary>When the verified purchase was linked to the subject (UTC).</summary>
    public DateTimeOffset LinkedAt { get; }

    /// <summary>
    /// Links a verified purchase to the buyer subject. The identifiers come from the already-recorded
    /// <see cref="PurchaseTransaction"/> and the server-resolved authenticated buyer, so the link is never built
    /// from untrusted client input (the body of a verification request carries no subject identity — WHO is
    /// buying is the authenticated caller, resolved server-side).
    /// </summary>
    /// <param name="purchaseTransactionId">The recorded purchase transaction to link (non-empty).</param>
    /// <param name="subjectType">The kind of buyer subject.</param>
    /// <param name="subjectId">The buyer subject's id (non-empty).</param>
    /// <param name="linkedAt">When the purchase is linked to the subject.</param>
    /// <exception cref="ArgumentException">The purchase transaction id or subject id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The subject type is not a defined value.</exception>
    public static BillingAccountLink Link(
        Guid purchaseTransactionId,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        DateTimeOffset linkedAt)
        => new(Guid.CreateVersion7(), purchaseTransactionId, subjectType, subjectId, linkedAt);

    /// <summary>
    /// Whether this link belongs to exactly the given subject. Matching is exact on the (<see cref="SubjectType"/>,
    /// <see cref="SubjectId"/>) pair; an empty id matches nothing. This is the ownership check that distinguishes
    /// the buyer re-submitting their own receipt (idempotent) from a DIFFERENT subject trying to claim it
    /// (denied).
    /// </summary>
    public bool IsForSubject(EntitlementSubjectType subjectType, Guid subjectId)
        => subjectId != Guid.Empty && SubjectType == subjectType && SubjectId == subjectId;

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: the link id, the linked purchase id and
    /// the subject pair — identifiers and an enum only, never the verification proof or any receipt content
    /// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"BillingAccountLink {Id} purchaseTransaction={PurchaseTransactionId} subject={SubjectType}:{SubjectId}";
}
