using LiveCore.Api.Entitlements;
using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for the <see cref="BillingAccountLink"/> aggregate (CORE-MON-002, the buyer-linkage story of the
/// "Monetization v1" epic). A link binds one verified purchase to one buyer subject; these tests pin its
/// construction invariants, the UTC normalization of the link timestamp, and the
/// <see cref="BillingAccountLink.IsForSubject"/> ownership check that distinguishes the buyer re-submitting their
/// own receipt from a DIFFERENT subject trying to claim it (the security-relevant distinction the
/// one-subject-per-receipt rule rests on). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class BillingAccountLinkTests
{
    private static readonly DateTimeOffset _linkedAt = new(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Link_records_the_purchase_the_subject_and_a_utc_timestamp()
    {
        var purchaseTransactionId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();
        // A non-UTC offset must be normalized to UTC so the persisted timestamptz is offset-independent.
        var localLinkedAt = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.FromHours(2));

        var link = BillingAccountLink.Link(purchaseTransactionId, EntitlementSubjectType.User, subjectId, localLinkedAt);

        Assert.NotEqual(Guid.Empty, link.Id);
        Assert.Equal(purchaseTransactionId, link.PurchaseTransactionId);
        Assert.Equal(EntitlementSubjectType.User, link.SubjectType);
        Assert.Equal(subjectId, link.SubjectId);
        Assert.Equal(TimeSpan.Zero, link.LinkedAt.Offset);
        Assert.Equal(localLinkedAt.ToUniversalTime(), link.LinkedAt);
    }

    [Fact]
    public void Link_rejects_an_empty_purchase_transaction_id()
    {
        var subjectId = Guid.CreateVersion7();
        Assert.Throws<ArgumentException>(
            () => BillingAccountLink.Link(Guid.Empty, EntitlementSubjectType.User, subjectId, _linkedAt));
    }

    [Fact]
    public void Link_rejects_an_empty_subject_id()
    {
        var purchaseTransactionId = Guid.CreateVersion7();
        Assert.Throws<ArgumentException>(
            () => BillingAccountLink.Link(purchaseTransactionId, EntitlementSubjectType.User, Guid.Empty, _linkedAt));
    }

    [Fact]
    public void Link_rejects_an_undefined_subject_type()
    {
        var purchaseTransactionId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BillingAccountLink.Link(purchaseTransactionId, (EntitlementSubjectType)999, subjectId, _linkedAt));
    }

    [Fact]
    public void IsForSubject_matches_only_the_exact_subject_pair()
    {
        var subjectId = Guid.CreateVersion7();
        var link = BillingAccountLink.Link(Guid.CreateVersion7(), EntitlementSubjectType.User, subjectId, _linkedAt);

        // The owning buyer matches (idempotent re-submission); a different id or a different kind does not (a
        // foreign claim), and an empty id matches nothing — the fail-closed default.
        Assert.True(link.IsForSubject(EntitlementSubjectType.User, subjectId));
        Assert.False(link.IsForSubject(EntitlementSubjectType.User, Guid.CreateVersion7()));
        Assert.False(link.IsForSubject(EntitlementSubjectType.Workspace, subjectId));
        Assert.False(link.IsForSubject(EntitlementSubjectType.User, Guid.Empty));
    }

    [Fact]
    public void ToString_carries_identifiers_only()
    {
        var purchaseTransactionId = Guid.CreateVersion7();
        var subjectId = Guid.CreateVersion7();
        var link = BillingAccountLink.Link(purchaseTransactionId, EntitlementSubjectType.User, subjectId, _linkedAt);

        var text = link.ToString();

        Assert.Contains(link.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(purchaseTransactionId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(subjectId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(nameof(EntitlementSubjectType.User), text, StringComparison.Ordinal);
    }
}
