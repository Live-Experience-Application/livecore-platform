using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for <see cref="PurchaseTransaction"/> (CORE-STORE-002) — the persisted record of a verified store
/// purchase. They pin the aggregate's invariants: a verified purchase is recorded in the <c>Active</c> state
/// from a normalized <see cref="VerifiedPurchase"/>, a status change is an idempotent state machine (a real move
/// reports a change, a move to the same status is a no-op), and the log-safe <c>ToString</c> carries only
/// identifiers and the status — never any secret (the proof is never stored). Generic Store vocabulary only
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class PurchaseTransactionTests
{
    private static readonly DateTimeOffset _recordedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static VerifiedPurchase Purchase(
        PurchaseProvider provider = PurchaseProvider.Apple,
        string transactionId = "txn-1",
        string product = "product.premium")
        => VerifiedPurchase.Create(provider, transactionId, product);

    [Fact]
    public void Record_persists_a_verified_purchase_as_active()
    {
        var transaction = PurchaseTransaction.Record(Purchase(PurchaseProvider.Google, "order-9", "product.plus"), _recordedAt);

        Assert.NotEqual(Guid.Empty, transaction.Id);
        Assert.Equal(PurchaseProvider.Google, transaction.Provider);
        Assert.Equal("order-9", transaction.ProviderTransactionId);
        Assert.Equal("product.plus", transaction.ProductReference);
        Assert.Equal(PurchaseTransactionStatus.Active, transaction.Status);
        Assert.Equal(_recordedAt, transaction.RecordedAt);
        Assert.Equal(_recordedAt, transaction.UpdatedAt);
    }

    [Fact]
    public void Record_rejects_a_null_purchase()
        => Assert.Throws<ArgumentNullException>(() => PurchaseTransaction.Record(null!, _recordedAt));

    [Fact]
    public void ChangeStatus_to_a_different_status_reports_a_change_and_advances_the_timestamp()
    {
        var transaction = PurchaseTransaction.Record(Purchase(), _recordedAt);

        var changed = transaction.ChangeStatus(PurchaseTransactionStatus.Refunded, _recordedAt.AddHours(1));

        Assert.True(changed);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);
        Assert.Equal(_recordedAt.AddHours(1), transaction.UpdatedAt);
    }

    [Fact]
    public void ChangeStatus_to_the_same_status_is_an_idempotent_no_op()
    {
        var transaction = PurchaseTransaction.Record(Purchase(), _recordedAt);

        var changed = transaction.ChangeStatus(PurchaseTransactionStatus.Active, _recordedAt.AddHours(1));

        Assert.False(changed);
        Assert.Equal(PurchaseTransactionStatus.Active, transaction.Status);
        Assert.Equal(_recordedAt, transaction.UpdatedAt); // unchanged: a no-op never advances the timestamp
    }

    [Theory]
    [InlineData(PurchaseTransactionStatus.Active)]
    [InlineData(PurchaseTransactionStatus.InGracePeriod)]
    [InlineData(PurchaseTransactionStatus.Cancelled)]
    public void ChangeStatus_out_of_a_refunded_purchase_is_a_no_op_revoked_stays_revoked(
        PurchaseTransactionStatus attemptedStatus)
    {
        // A refund is terminal (CORE-MON-004): once Refunded, no later notification — a renewal back to Active, a
        // grace period, or even a cancellation — can move the purchase. The attempt is a no-op (no state change, no
        // advanced timestamp), so a later DID_RENEW can never resurrect a refunded purchase and re-grant premium.
        var transaction = PurchaseTransaction.Record(Purchase(), _recordedAt);
        Assert.True(transaction.ChangeStatus(PurchaseTransactionStatus.Refunded, _recordedAt.AddHours(1)));

        var changed = transaction.ChangeStatus(attemptedStatus, _recordedAt.AddHours(2));

        Assert.False(changed);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);
        Assert.Equal(_recordedAt.AddHours(1), transaction.UpdatedAt); // the blocked move never advances the timestamp
    }

    [Fact]
    public void ChangeStatus_out_of_a_cancelled_purchase_is_a_no_op()
    {
        // Cancelled is likewise terminal.
        var transaction = PurchaseTransaction.Record(Purchase(), _recordedAt);
        Assert.True(transaction.ChangeStatus(PurchaseTransactionStatus.Cancelled, _recordedAt.AddHours(1)));

        var changed = transaction.ChangeStatus(PurchaseTransactionStatus.Active, _recordedAt.AddHours(2));

        Assert.False(changed);
        Assert.Equal(PurchaseTransactionStatus.Cancelled, transaction.Status);
    }

    [Fact]
    public void ChangeStatus_still_allows_a_grace_period_to_renew_back_to_active()
    {
        // The absorbing rule applies ONLY to revoked states; a legitimate grace-period -> renewal recovery is
        // unaffected.
        var transaction = PurchaseTransaction.Record(Purchase(), _recordedAt);
        Assert.True(transaction.ChangeStatus(PurchaseTransactionStatus.InGracePeriod, _recordedAt.AddHours(1)));

        var changed = transaction.ChangeStatus(PurchaseTransactionStatus.Active, _recordedAt.AddHours(2));

        Assert.True(changed);
        Assert.Equal(PurchaseTransactionStatus.Active, transaction.Status);
        Assert.Equal(_recordedAt.AddHours(2), transaction.UpdatedAt);
    }

    [Fact]
    public void ChangeStatus_rejects_an_undefined_status()
    {
        var transaction = PurchaseTransaction.Record(Purchase(), _recordedAt);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => transaction.ChangeStatus((PurchaseTransactionStatus)999, _recordedAt.AddHours(1)));
    }

    [Fact]
    public void ToString_carries_identifiers_and_status_for_logs()
    {
        var transaction = PurchaseTransaction.Record(Purchase(PurchaseProvider.Apple, "txn-77", "product.premium"), _recordedAt);

        var text = transaction.ToString();

        Assert.Contains("Apple", text, StringComparison.Ordinal);
        Assert.Contains("txn-77", text, StringComparison.Ordinal);
        Assert.Contains("product.premium", text, StringComparison.Ordinal);
        Assert.Contains("Active", text, StringComparison.Ordinal);
    }
}
