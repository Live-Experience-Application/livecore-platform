// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.Store;

/// <summary>
/// Links a verified purchase to its authenticated BUYER and enforces the one-subject-per-receipt rule
/// (CORE-MON-002, the buyer-linkage story of the "Monetization v1" epic). It is the application service that
/// turns a recorded <see cref="PurchaseTransaction"/> plus the server-resolved buyer subject into a durable
/// <see cref="BillingAccountLink"/>, so a verified purchase is no longer buyer-less and CORE-MON-003 can grant
/// that subject the mapped <see cref="SubjectEntitlement"/>.
///
/// FAIL-CLOSED AND IDEMPOTENT. A purchase already linked to the SAME buyer (a client retry, a replayed-but-genuine
/// proof) is recognized and returns <see cref="BillingAccountLinkOutcome.AlreadyLinked"/> with no second row. A
/// purchase already linked to a DIFFERENT subject returns
/// <see cref="BillingAccountLinkOutcome.ConflictDifferentSubject"/> and persists nothing — the same external
/// receipt can never be claimed by two different subjects, so "user B cannot bind user A's transaction/receipt"
/// (the story acceptance criterion; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The uniqueness is enforced at
/// the database by the unique <c>billing_account_links(purchase_transaction_id)</c> index, so a concurrent claim
/// race is resolved fail-closed: the loser re-reads the winner's link and is reported as already-linked or
/// conflicting accordingly (the same shape <see cref="PurchaseTransactionService"/> uses for the recording race).
///
/// AUTHORIZATION IS UPSTREAM. This service performs the linkage mechanism only; it does not authenticate the
/// caller or verify a proof. The verification endpoints that TRIGGER a link (the Apple CORE-STORE-003 and Google
/// CORE-STORE-004 routes) authorize the caller server-side, verify the proof and record the purchase, then resolve
/// the authenticated buyer's subject and hand it here — inside the same transaction as the recording so the buyer
/// linkage is durably atomic with the purchase.
/// </summary>
public sealed class BillingAccountLinkService
{
    private readonly IBillingAccountLinkRepository _links;

    public BillingAccountLinkService(IBillingAccountLinkRepository links)
    {
        ArgumentNullException.ThrowIfNull(links);
        _links = links;
    }

    /// <summary>
    /// Links a recorded purchase to the buyer subject, enforcing that a receipt maps to exactly one subject. If
    /// the purchase is not yet linked it persists a new <see cref="BillingAccountLink"/> and returns
    /// <see cref="BillingAccountLinkOutcome.Linked"/>. If it is already linked to THIS subject it returns
    /// <see cref="BillingAccountLinkOutcome.AlreadyLinked"/> (idempotent). If it is already linked to a DIFFERENT
    /// subject it returns <see cref="BillingAccountLinkOutcome.ConflictDifferentSubject"/> and persists nothing
    /// (fail-closed).
    /// </summary>
    /// <param name="purchaseTransactionId">The recorded purchase transaction to link (non-empty).</param>
    /// <param name="subjectType">The kind of buyer subject.</param>
    /// <param name="subjectId">The buyer subject's id (non-empty).</param>
    /// <param name="linkedAt">When the purchase is linked.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The purchase transaction id or subject id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The subject type is not a defined value.</exception>
    public async Task<BillingAccountLinkResult> LinkBuyerAsync(
        Guid purchaseTransactionId,
        EntitlementSubjectType subjectType,
        Guid subjectId,
        DateTimeOffset linkedAt,
        CancellationToken cancellationToken)
    {
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

        // Already linked? Decide by the subject pair: the same buyer is an idempotent no-op; a different subject is
        // a fail-closed conflict (the receipt is already claimed).
        var existing = await _links
            .FindByPurchaseTransactionAsync(purchaseTransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Resolve(existing, subjectType, subjectId);
        }

        var link = BillingAccountLink.Link(purchaseTransactionId, subjectType, subjectId, linkedAt);
        var addResult = await _links.AddAsync(link, cancellationToken).ConfigureAwait(false);
        if (addResult == BillingAccountLinkAddResult.Added)
        {
            return new BillingAccountLinkResult(BillingAccountLinkOutcome.Linked, link);
        }

        // A concurrent link won the race between the find and the insert (the unique purchase_transaction_id index
        // rejected this insert). Re-read the winning row and decide by its subject: the same buyer is already
        // linked, a different one is a conflict — never a second row.
        var raced = await _links
            .FindByPurchaseTransactionAsync(purchaseTransactionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The billing account link insert reported a duplicate purchase, but no link exists for the purchase.");

        return Resolve(raced, subjectType, subjectId);
    }

    private static BillingAccountLinkResult Resolve(
        BillingAccountLink existing,
        EntitlementSubjectType subjectType,
        Guid subjectId)
        => existing.IsForSubject(subjectType, subjectId)
            ? new BillingAccountLinkResult(BillingAccountLinkOutcome.AlreadyLinked, existing)
            : new BillingAccountLinkResult(BillingAccountLinkOutcome.ConflictDifferentSubject, existing);
}
