using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for <see cref="PurchaseVerificationResult"/> (CORE-STORE-001) — the provider-neutral outcome of
/// verification. The factories make "verified with a purchase" and "rejected with a reason" mutually exclusive
/// by construction, so a caller can never read a purchase off a rejected result (fail-closed: no entitlement is
/// granted without a verified purchase; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). Generic Store
/// vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class PurchaseVerificationResultTests
{
    private static readonly VerifiedPurchase _purchase =
        VerifiedPurchase.Create(PurchaseProvider.Apple, "txn-1", "product.premium");

    [Fact]
    public void Verified_carries_the_purchase_and_no_rejection_reason()
    {
        var result = PurchaseVerificationResult.Verified(_purchase);

        Assert.True(result.IsVerified);
        Assert.Equal(PurchaseVerificationStatus.Verified, result.Status);
        Assert.Same(_purchase, result.Purchase);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void Rejected_carries_a_reason_and_no_purchase()
    {
        var result = PurchaseVerificationResult.Rejected("  invalid or unverifiable proof  ");

        Assert.False(result.IsVerified);
        Assert.Equal(PurchaseVerificationStatus.Rejected, result.Status);
        Assert.Null(result.Purchase);
        Assert.Equal("invalid or unverifiable proof", result.RejectionReason);
    }

    [Fact]
    public void Verified_rejects_a_null_purchase()
        => Assert.Throws<ArgumentNullException>(() => PurchaseVerificationResult.Verified(null!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejected_requires_a_reason(string? reason)
        => Assert.Throws<ArgumentException>(() => PurchaseVerificationResult.Rejected(reason!));

    [Fact]
    public void ToString_is_log_safe_for_both_outcomes()
    {
        var verified = PurchaseVerificationResult.Verified(_purchase).ToString();
        Assert.Contains("Verified", verified, StringComparison.Ordinal);
        Assert.Contains("txn-1", verified, StringComparison.Ordinal);

        var rejected = PurchaseVerificationResult.Rejected("invalid proof").ToString();
        Assert.Contains("Rejected", rejected, StringComparison.Ordinal);
        Assert.Contains("invalid proof", rejected, StringComparison.Ordinal);
    }
}
