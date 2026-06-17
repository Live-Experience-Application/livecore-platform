// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Unit tests for <see cref="EffectiveEntitlements"/> (CORE-ENTL-002) — the subject's resolved, server-side
/// premium state and the fail-closed query surface behind "User-visible premium state comes only from server
/// entitlements" (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The tests pin the POSITIVE answers (a held
/// flag/quota resolves to its granted value) and the NEGATIVE, fail-closed defaults (an absent entitlement is
/// never enabled and reports no quota), so a client can never read premium state the server did not grant.
/// </summary>
public class EffectiveEntitlementsTests
{
    private static readonly DateTimeOffset _grantedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _subjectId = Guid.CreateVersion7();
    private static readonly Guid _planId = Guid.CreateVersion7();

    private static SubjectEntitlement Flag(string key, bool value)
    {
        var definition = EntitlementDefinition.Define(key, EntitlementValueKind.Flag, "Flag", null, _grantedAt);
        return SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, _subjectId, definition, value, _planId, _grantedAt);
    }

    private static SubjectEntitlement Quota(string key, long? limit)
    {
        var definition = EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Quota", null, _grantedAt);
        return SubjectEntitlement.GrantQuota(EntitlementSubjectType.User, _subjectId, definition, limit, _planId, _grantedAt);
    }

    // --- Positive lookups -------------------------------------------------------

    [Fact]
    public void IsFlagEnabled_is_true_for_a_flag_granted_true()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Flag("ads.disabled", true)]);

        Assert.True(effective.IsFlagEnabled("ads.disabled"));
        Assert.True(effective.Has("ads.disabled"));
    }

    [Fact]
    public void IsFlagEnabled_canonicalizes_the_lookup_key()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Flag("ads.disabled", true)]);

        // A differently-cased / padded input resolves the same stored canonical key.
        Assert.True(effective.IsFlagEnabled("  ADS.DISABLED  "));
    }

    [Fact]
    public void TryGetQuotaLimit_returns_the_granted_limit()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Quota("session.participant.max", 4)]);

        Assert.True(effective.TryGetQuotaLimit("session.participant.max", out var limit));
        Assert.Equal(4, limit);
    }

    [Fact]
    public void TryGetQuotaLimit_distinguishes_an_unlimited_grant_from_no_entitlement()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Quota("workspace.active.max", null)]);

        // Held, unlimited: true with a null limit.
        Assert.True(effective.TryGetQuotaLimit("workspace.active.max", out var unlimited));
        Assert.Null(unlimited);

        // Not held: false with a null limit — the caller must distinguish this from "unlimited".
        Assert.False(effective.TryGetQuotaLimit("session.participant.max", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void All_is_ordered_by_key()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments(
            [Quota("workspace.active.max", 1), Flag("ads.disabled", true), Flag("export.enabled", true)]);

        Assert.Equal(
            new[] { "ads.disabled", "export.enabled", "workspace.active.max" },
            effective.All.Select(entitlement => entitlement.Key).ToArray());
        Assert.Equal(3, effective.Count);
    }

    // --- Negative, fail-closed lookups ------------------------------------------

    [Fact]
    public void An_absent_flag_is_not_enabled()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Flag("ads.disabled", true)]);

        Assert.False(effective.IsFlagEnabled("export.enabled")); // never granted
        Assert.False(effective.Has("export.enabled"));
    }

    [Fact]
    public void A_flag_granted_false_is_not_enabled()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Flag("ads.disabled", false)]);

        Assert.True(effective.Has("ads.disabled")); // the subject holds it...
        Assert.False(effective.IsFlagEnabled("ads.disabled")); // ...but it is not enabled
    }

    [Fact]
    public void A_quota_entitlement_is_not_an_enabled_flag()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Quota("workspace.active.max", 1)]);

        Assert.False(effective.IsFlagEnabled("workspace.active.max"));
    }

    [Fact]
    public void A_flag_entitlement_reports_no_quota()
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Flag("ads.disabled", true)]);

        Assert.False(effective.TryGetQuotaLimit("ads.disabled", out var limit));
        Assert.Null(limit);
    }

    [Fact]
    public void The_empty_state_is_entitled_to_nothing()
    {
        Assert.Empty(EffectiveEntitlements.Empty.All);
        Assert.Equal(0, EffectiveEntitlements.Empty.Count);
        Assert.False(EffectiveEntitlements.Empty.IsFlagEnabled("ads.disabled"));
        Assert.False(EffectiveEntitlements.Empty.TryGetQuotaLimit("workspace.active.max", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a valid key")]
    public void A_malformed_lookup_key_resolves_to_not_held(string key)
    {
        var effective = EffectiveEntitlements.FromActiveAssignments([Flag("ads.disabled", true)]);

        Assert.Null(effective.Find(key));
        Assert.False(effective.IsFlagEnabled(key));
        Assert.False(effective.TryGetQuotaLimit(key, out _));
    }

    // --- Data invariant ---------------------------------------------------------

    [Fact]
    public void Duplicate_keys_in_the_input_are_rejected()
    {
        // A subject holds each entitlement at most once (the unique per-subject index); a duplicate key in the
        // resolved input is an invariant violation, not silently collapsed.
        Assert.Throws<ArgumentException>(() => EffectiveEntitlements.FromActiveAssignments(
            [Flag("ads.disabled", true), Flag("ads.disabled", false)]));
    }
}
