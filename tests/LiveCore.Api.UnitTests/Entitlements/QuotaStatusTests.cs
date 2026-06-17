// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// QUOTA CALCULATION and BOUNDARY tests for <see cref="QuotaStatus.Calculate"/> (CORE-ENTL-003) — the required
/// tests of the quota definition and quota status story. <see cref="QuotaStatus.Calculate"/> is the single, pure
/// place the server-side quota math lives, so these tests pin the acceptance criterion "Quota status is calculated
/// server-side for subjects and workspaces" exhaustively at every boundary, without a database:
/// <list type="bullet">
///   <item>under / at / over the limit (the boundary: usage exactly AT the cap is allowed, not exceeded);</item>
///   <item>an unlimited (fair-use) grant — never exceeded, no finite remaining;</item>
///   <item>a zero limit and a NOT-entitled subject — fail-closed, no allowance (any usage exceeds);</item>
///   <item>guards on malformed input.</item>
/// </list>
/// All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class QuotaStatusTests
{
    private const string _key = "workspace.active.max";

    // --- Under / at / over the limit (the boundary) -----------------------------

    [Fact]
    public void Under_the_limit_reports_the_remaining_headroom()
    {
        var status = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: 5, used: 2);

        Assert.Equal(_key, status.Key);
        Assert.Equal(QuotaUnit.Count, status.Unit);
        Assert.True(status.IsEntitled);
        Assert.False(status.IsUnlimited);
        Assert.Equal(2, status.Used);
        Assert.Equal(5, status.Limit);
        Assert.Equal(3, status.Remaining);
        Assert.False(status.IsExceeded);
    }

    [Fact]
    public void Usage_exactly_at_the_limit_is_the_boundary_and_is_not_exceeded()
    {
        var status = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: 5, used: 5);

        // The cap is the maximum allowed amount: at the cap there is zero remaining but it is NOT exceeded.
        Assert.Equal(0, status.Remaining);
        Assert.False(status.IsExceeded);
    }

    [Fact]
    public void Usage_over_the_limit_is_exceeded_with_zero_remaining()
    {
        var status = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: 5, used: 6);

        Assert.Equal(0, status.Remaining); // never negative
        Assert.True(status.IsExceeded);
    }

    [Fact]
    public void Zero_usage_reports_the_full_limit_as_remaining()
    {
        var status = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: 5, used: 0);

        Assert.Equal(5, status.Remaining);
        Assert.False(status.IsExceeded);
    }

    // --- Zero limit -------------------------------------------------------------

    [Fact]
    public void A_granted_zero_limit_allows_no_usage()
    {
        var none = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: 0, used: 0);
        Assert.Equal(0, none.Remaining);
        Assert.False(none.IsExceeded);

        var over = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: 0, used: 1);
        Assert.Equal(0, over.Remaining);
        Assert.True(over.IsExceeded);
    }

    // --- Unlimited (fair-use) ---------------------------------------------------

    [Fact]
    public void An_unlimited_grant_is_never_exceeded_and_has_no_finite_remaining()
    {
        var status = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: null, used: 1_000_000);

        Assert.True(status.IsEntitled);
        Assert.True(status.IsUnlimited);
        Assert.Null(status.Limit);
        Assert.Null(status.Remaining);
        Assert.False(status.IsExceeded);
        Assert.Equal(1_000_000, status.Used);
    }

    // --- Not entitled (fail-closed) ---------------------------------------------

    [Fact]
    public void A_not_entitled_subject_has_no_allowance()
    {
        // No entitlement => no headroom: the limit is treated as zero, so any usage already exceeds it (a client
        // can never obtain headroom the server did not grant; docs/21).
        var idle = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: false, limit: null, used: 0);
        Assert.False(idle.IsEntitled);
        Assert.False(idle.IsUnlimited);
        Assert.Equal(0, idle.Limit);
        Assert.Equal(0, idle.Remaining);
        Assert.False(idle.IsExceeded);

        var using1 = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: false, limit: null, used: 1);
        Assert.False(using1.IsEntitled);
        Assert.Equal(0, using1.Remaining);
        Assert.True(using1.IsExceeded);
    }

    [Fact]
    public void A_not_entitled_subject_ignores_any_supplied_limit()
    {
        // Defence in depth: even if a (stale) limit is passed, a not-entitled subject still has no allowance.
        var status = QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: false, limit: 99, used: 1);

        Assert.False(status.IsEntitled);
        Assert.Equal(0, status.Limit);
        Assert.True(status.IsExceeded);
    }

    // --- Bytes unit -------------------------------------------------------------

    [Fact]
    public void The_bytes_unit_is_carried_through()
    {
        const string storageKey = "asset.storage.bytes.max";
        var status = QuotaStatus.Calculate(storageKey, QuotaUnit.Bytes, isEntitled: true, limit: 262144000, used: 1024);

        Assert.Equal(QuotaUnit.Bytes, status.Unit);
        Assert.Equal(262144000 - 1024, status.Remaining);
        Assert.False(status.IsExceeded);
    }

    // --- Guards -----------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Workspace.Active.Max")] // not canonical lower-case
    [InlineData("workspace..active")] // doubled dot
    public void Calculate_rejects_a_malformed_key(string key)
        => Assert.Throws<ArgumentException>(() =>
            QuotaStatus.Calculate(key, QuotaUnit.Count, isEntitled: true, limit: 1, used: 0));

    [Fact]
    public void Calculate_rejects_a_negative_usage()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: 1, used: -1));

    [Fact]
    public void Calculate_rejects_a_negative_limit()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuotaStatus.Calculate(_key, QuotaUnit.Count, isEntitled: true, limit: -1, used: 0));

    [Fact]
    public void Calculate_rejects_an_undefined_unit()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuotaStatus.Calculate(_key, (QuotaUnit)999, isEntitled: true, limit: 1, used: 0));
}
