// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for <see cref="PurchaseTransactionStatusMachine"/> (CORE-MON-004) — the monotonic purchase-status
/// state machine. They pin the story's invariant that a revoked state is TERMINAL (absorbing): a refund/cancellation
/// stays revoked and a later renewal can neither transition out of it nor change the converged status reconciliation
/// re-derives. Generic Store vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class PurchaseTransactionStatusMachineTests
{
    private static readonly DateTimeOffset _t1 = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _t2 = new(2026, 6, 12, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _t3 = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PurchaseTransactionStatus.Refunded, true)]
    [InlineData(PurchaseTransactionStatus.Cancelled, true)]
    [InlineData(PurchaseTransactionStatus.Active, false)]
    [InlineData(PurchaseTransactionStatus.InGracePeriod, false)]
    public void IsRevoked_identifies_the_terminal_states(PurchaseTransactionStatus status, bool expected)
        => Assert.Equal(expected, PurchaseTransactionStatusMachine.IsRevoked(status));

    [Theory]
    // From a non-revoked state every target is permitted (renewals, grace, and into a revoked state).
    [InlineData(PurchaseTransactionStatus.Active, PurchaseTransactionStatus.InGracePeriod, true)]
    [InlineData(PurchaseTransactionStatus.Active, PurchaseTransactionStatus.Refunded, true)]
    [InlineData(PurchaseTransactionStatus.Active, PurchaseTransactionStatus.Cancelled, true)]
    [InlineData(PurchaseTransactionStatus.InGracePeriod, PurchaseTransactionStatus.Active, true)]
    [InlineData(PurchaseTransactionStatus.InGracePeriod, PurchaseTransactionStatus.Refunded, true)]
    // From a revoked (absorbing) state no move to a DIFFERENT status is permitted.
    [InlineData(PurchaseTransactionStatus.Refunded, PurchaseTransactionStatus.Active, false)]
    [InlineData(PurchaseTransactionStatus.Refunded, PurchaseTransactionStatus.InGracePeriod, false)]
    [InlineData(PurchaseTransactionStatus.Refunded, PurchaseTransactionStatus.Cancelled, false)]
    [InlineData(PurchaseTransactionStatus.Cancelled, PurchaseTransactionStatus.Active, false)]
    [InlineData(PurchaseTransactionStatus.Cancelled, PurchaseTransactionStatus.Refunded, false)]
    // A move to the SAME status is always permitted (an idempotent no-op), even from a revoked state.
    [InlineData(PurchaseTransactionStatus.Refunded, PurchaseTransactionStatus.Refunded, true)]
    [InlineData(PurchaseTransactionStatus.Active, PurchaseTransactionStatus.Active, true)]
    public void CanTransitionTo_enforces_absorbing_revoked_states(
        PurchaseTransactionStatus current,
        PurchaseTransactionStatus target,
        bool expected)
        => Assert.Equal(expected, PurchaseTransactionStatusMachine.CanTransitionTo(current, target));

    [Fact]
    public void Converge_over_no_effects_is_active_with_no_determining_time()
    {
        var (status, determinedAt) = PurchaseTransactionStatusMachine.Converge(
            Array.Empty<(PurchaseTransactionStatus, DateTimeOffset, Guid)>());

        Assert.Equal(PurchaseTransactionStatus.Active, status);
        Assert.Null(determinedAt);
    }

    [Fact]
    public void Converge_follows_the_latest_event_among_non_revoking_effects()
    {
        // A grace period (later) then a renewal (latest) converge to Active; ordering is by event time, not list order.
        var (status, determinedAt) = PurchaseTransactionStatusMachine.Converge(new[]
        {
            (PurchaseTransactionStatus.InGracePeriod, _t1, Guid.CreateVersion7()),
            (PurchaseTransactionStatus.Active, _t2, Guid.CreateVersion7()),
        });

        Assert.Equal(PurchaseTransactionStatus.Active, status);
        Assert.Equal(_t2, determinedAt);
    }

    [Fact]
    public void Converge_makes_a_refund_absorbing_even_when_a_later_renewal_exists()
    {
        // The refund occurred BEFORE the renewal, but a revoked state is absorbing, so the later renewal is skipped
        // and the purchase converges to Refunded — a renewal cannot resurrect a refund.
        var (status, determinedAt) = PurchaseTransactionStatusMachine.Converge(new[]
        {
            (PurchaseTransactionStatus.Refunded, _t1, Guid.CreateVersion7()),
            (PurchaseTransactionStatus.Active, _t2, Guid.CreateVersion7()),
        });

        Assert.Equal(PurchaseTransactionStatus.Refunded, status);
        Assert.Equal(_t1, determinedAt); // the refund determined the converged state
    }

    [Fact]
    public void Converge_makes_a_cancellation_absorbing_even_when_a_later_renewal_exists()
    {
        // CORE-MON-011 (the "Cancelled equals immediate-revoke" contract, docs/21): a Cancelled purchase that later
        // receives a renewal stays Cancelled — Cancelled is absorbing exactly like Refunded, so the later renewal is
        // skipped and the purchase converges to Cancelled. This pins the "a Cancelled then later Renewed stays
        // revoked" semantics at the pure-machine level (the same shape as the Refunded test above).
        var (status, determinedAt) = PurchaseTransactionStatusMachine.Converge(new[]
        {
            (PurchaseTransactionStatus.Cancelled, _t1, Guid.CreateVersion7()),
            (PurchaseTransactionStatus.Active, _t2, Guid.CreateVersion7()),
        });

        Assert.Equal(PurchaseTransactionStatus.Cancelled, status);
        Assert.Equal(_t1, determinedAt); // the cancellation determined the converged state
    }

    [Fact]
    public void Converge_is_order_independent_and_the_first_revoking_event_by_time_wins()
    {
        // Events presented out of order; the earliest-by-event-time revoking event (the cancellation at _t1) is
        // absorbing, so the later refund (_t3) and the interleaved renewal (_t2) are all skipped.
        var (status, _) = PurchaseTransactionStatusMachine.Converge(new[]
        {
            (PurchaseTransactionStatus.Refunded, _t3, Guid.CreateVersion7()),
            (PurchaseTransactionStatus.Active, _t2, Guid.CreateVersion7()),
            (PurchaseTransactionStatus.Cancelled, _t1, Guid.CreateVersion7()),
        });

        Assert.Equal(PurchaseTransactionStatus.Cancelled, status);
    }
}
