using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for <see cref="PurchaseEvent"/> (CORE-STORE-002) — one immutable entry in a purchase's append-only
/// audit trail. They pin: the initial recording has no previous status and a resulting <c>Active</c> status, a
/// status-change event captures the before/after pair, an event must record a real change (the new status must
/// differ from the previous), and the log-safe <c>ToString</c> carries only identifiers and status names.
/// Generic Store vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class PurchaseEventTests
{
    private static readonly DateTimeOffset _at = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForRecorded_has_no_previous_status_and_a_new_status_of_active()
    {
        var transactionId = Guid.CreateVersion7();

        var purchaseEvent = PurchaseEvent.ForRecorded(transactionId, _at);

        Assert.NotEqual(Guid.Empty, purchaseEvent.Id);
        Assert.Equal(transactionId, purchaseEvent.PurchaseTransactionId);
        Assert.Null(purchaseEvent.PreviousStatus);
        Assert.Equal(PurchaseTransactionStatus.Active, purchaseEvent.NewStatus);
        Assert.Equal(_at, purchaseEvent.CreatedAt);
    }

    [Fact]
    public void ForRecorded_rejects_an_empty_transaction_id()
        => Assert.Throws<ArgumentException>(() => PurchaseEvent.ForRecorded(Guid.Empty, _at));

    [Fact]
    public void ForStatusChange_captures_the_before_and_after_state()
    {
        var transactionId = Guid.CreateVersion7();

        var purchaseEvent = PurchaseEvent.ForStatusChange(
            transactionId, PurchaseTransactionStatus.Active, PurchaseTransactionStatus.Refunded, _at);

        Assert.Equal(PurchaseTransactionStatus.Active, purchaseEvent.PreviousStatus);
        Assert.Equal(PurchaseTransactionStatus.Refunded, purchaseEvent.NewStatus);
    }

    [Fact]
    public void ForStatusChange_rejects_a_no_op_transition()
        => Assert.Throws<ArgumentException>(() => PurchaseEvent.ForStatusChange(
            Guid.CreateVersion7(),
            PurchaseTransactionStatus.Active,
            PurchaseTransactionStatus.Active,
            _at));

    [Fact]
    public void ForStatusChange_rejects_an_empty_transaction_id()
        => Assert.Throws<ArgumentException>(() => PurchaseEvent.ForStatusChange(
            Guid.Empty,
            PurchaseTransactionStatus.Active,
            PurchaseTransactionStatus.Cancelled,
            _at));

    [Fact]
    public void ForStatusChange_rejects_an_undefined_status()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PurchaseEvent.ForStatusChange(
            Guid.CreateVersion7(),
            PurchaseTransactionStatus.Active,
            (PurchaseTransactionStatus)999,
            _at));

    [Fact]
    public void ToString_carries_the_status_pair_for_logs()
    {
        var purchaseEvent = PurchaseEvent.ForStatusChange(
            Guid.CreateVersion7(), PurchaseTransactionStatus.Active, PurchaseTransactionStatus.InGracePeriod, _at);

        var text = purchaseEvent.ToString();

        Assert.Contains("Active", text, StringComparison.Ordinal);
        Assert.Contains("InGracePeriod", text, StringComparison.Ordinal);
    }
}
