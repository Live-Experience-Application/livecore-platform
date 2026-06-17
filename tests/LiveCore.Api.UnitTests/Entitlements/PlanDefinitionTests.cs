// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Unit tests for the <see cref="PlanDefinition"/> aggregate, its grant factories
/// (<see cref="PlanDefinition.GrantFlag"/> / <see cref="PlanDefinition.GrantQuota"/>) and the
/// <see cref="PlanEntitlement"/> value invariants (CORE-ENTL-001). A plan is the generic, product-neutral
/// bundle of granted entitlements (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md).
///
/// The fail-closed grant rules are the security-relevant core of this story: the value shape of a grant is
/// fixed by the referenced entitlement definition's <see cref="EntitlementValueKind"/>, so a plan can never
/// bind a boolean to a quota or a number to a flag (docs/21 "Never trust client-side premium flags"); a retired
/// entitlement cannot be granted; and an entitlement is granted at most once per plan. All fixtures use generic
/// Core keys and display text only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class PlanDefinitionTests
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private static PlanDefinition StandardPlan()
        => PlanDefinition.Define("standard", "Standard plan", "A generic mid-tier plan.", _createdAt);

    private static EntitlementDefinition QuotaEntitlement(string key = "workspace.active.max")
        => EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Active workspace limit", null, _createdAt);

    private static EntitlementDefinition FlagEntitlement(string key = "ads.disabled")
        => EntitlementDefinition.Define(key, EntitlementValueKind.Flag, "Ad-free experience", null, _createdAt);

    // --- Factory: Define --------------------------------------------------------

    [Fact]
    public void Define_builds_an_active_plan_with_no_grants()
    {
        var plan = StandardPlan();

        Assert.NotEqual(Guid.Empty, plan.Id);
        Assert.Equal(7, plan.Id.Version);
        Assert.Equal("standard", plan.Key);
        Assert.Equal("Standard plan", plan.DisplayName);
        Assert.True(plan.IsActive);
        Assert.Empty(plan.Entitlements);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Standard Plan")] // spaces are not a valid key
    public void Define_rejects_an_invalid_key(string? key)
        => Assert.Throws<ArgumentException>(
            () => PlanDefinition.Define(key!, "Standard plan", null, _createdAt));

    // --- Grants -----------------------------------------------------------------

    [Fact]
    public void GrantQuota_binds_a_numeric_limit_to_a_quota_entitlement()
    {
        var plan = StandardPlan();
        var entitlement = QuotaEntitlement();

        var grant = plan.GrantQuota(entitlement, 5);

        Assert.Single(plan.Entitlements);
        Assert.Equal(plan.Id, grant.PlanDefinitionId);
        Assert.Equal(entitlement.Id, grant.EntitlementDefinitionId);
        Assert.Equal(EntitlementValueKind.Quota, grant.ValueKind);
        Assert.True(grant.IsQuota);
        Assert.False(grant.IsFlag);
        Assert.Equal(5, grant.QuotaLimit);
        Assert.False(grant.IsUnlimitedQuota);
        Assert.Null(grant.FlagValue);
        Assert.True(plan.GrantsEntitlement(entitlement.Id));
    }

    [Fact]
    public void GrantQuota_with_a_null_limit_is_an_unlimited_fair_use_grant()
    {
        var plan = StandardPlan();
        var entitlement = QuotaEntitlement();

        var grant = plan.GrantQuota(entitlement, null);

        Assert.True(grant.IsUnlimitedQuota);
        Assert.Null(grant.QuotaLimit);
    }

    [Fact]
    public void GrantQuota_allows_a_zero_limit()
    {
        var plan = StandardPlan();

        var grant = plan.GrantQuota(QuotaEntitlement(), 0);

        Assert.Equal(0, grant.QuotaLimit);
        Assert.False(grant.IsUnlimitedQuota);
    }

    [Fact]
    public void GrantFlag_binds_a_boolean_to_a_flag_entitlement()
    {
        var plan = StandardPlan();
        var entitlement = FlagEntitlement();

        var grant = plan.GrantFlag(entitlement, true);

        Assert.Equal(EntitlementValueKind.Flag, grant.ValueKind);
        Assert.True(grant.IsFlag);
        Assert.Equal(true, grant.FlagValue);
        Assert.Null(grant.QuotaLimit);
        Assert.False(grant.IsUnlimitedQuota);
    }

    [Fact]
    public void A_plan_can_grant_several_distinct_entitlements()
    {
        var plan = StandardPlan();

        plan.GrantQuota(QuotaEntitlement("workspace.active.max"), 1);
        plan.GrantFlag(FlagEntitlement("ads.disabled"), false);
        plan.GrantQuota(QuotaEntitlement("session.participant.max"), 4);

        Assert.Equal(3, plan.Entitlements.Count);
    }

    // --- Fail-closed grant rules (value shape, lifecycle, uniqueness) -----------

    [Fact]
    public void GrantQuota_rejects_a_flag_entitlement()
    {
        // A plan can never bind a numeric limit to a boolean capability (value shape is fixed by the
        // definition's kind; docs/21).
        var plan = StandardPlan();

        Assert.Throws<ArgumentException>(() => plan.GrantQuota(FlagEntitlement(), 5));
        Assert.Empty(plan.Entitlements);
    }

    [Fact]
    public void GrantFlag_rejects_a_quota_entitlement()
    {
        var plan = StandardPlan();

        Assert.Throws<ArgumentException>(() => plan.GrantFlag(QuotaEntitlement(), true));
        Assert.Empty(plan.Entitlements);
    }

    [Fact]
    public void GrantQuota_rejects_a_negative_limit()
    {
        var plan = StandardPlan();

        Assert.Throws<ArgumentOutOfRangeException>(() => plan.GrantQuota(QuotaEntitlement(), -1));
        Assert.Empty(plan.Entitlements);
    }

    [Fact]
    public void Granting_an_inactive_entitlement_is_rejected()
    {
        // A retired (deactivated) entitlement is no longer offered, so a plan may not grant it (fail closed).
        var plan = StandardPlan();
        var entitlement = QuotaEntitlement();
        entitlement.Deactivate(_createdAt.AddHours(1));

        Assert.Throws<ArgumentException>(() => plan.GrantQuota(entitlement, 5));
        Assert.Empty(plan.Entitlements);
    }

    [Fact]
    public void Granting_the_same_entitlement_twice_is_rejected()
    {
        var plan = StandardPlan();
        var entitlement = QuotaEntitlement();
        plan.GrantQuota(entitlement, 5);

        Assert.Throws<InvalidOperationException>(() => plan.GrantQuota(entitlement, 9));
        Assert.Single(plan.Entitlements);
    }

    [Fact]
    public void GrantQuota_rejects_a_null_definition()
        => Assert.Throws<ArgumentNullException>(() => StandardPlan().GrantQuota(null!, 5));

    [Fact]
    public void GrantsEntitlement_is_false_for_an_unrelated_or_empty_id()
    {
        var plan = StandardPlan();
        plan.GrantQuota(QuotaEntitlement(), 5);

        Assert.False(plan.GrantsEntitlement(Guid.CreateVersion7()));
        Assert.False(plan.GrantsEntitlement(Guid.Empty));
    }

    // --- Soft lifecycle ---------------------------------------------------------

    [Fact]
    public void Deactivate_and_reactivate_toggle_the_offered_flag()
    {
        var plan = StandardPlan();

        plan.Deactivate(_createdAt.AddHours(1));
        Assert.False(plan.IsActive);

        plan.Reactivate(_createdAt.AddHours(2));
        Assert.True(plan.IsActive);
    }

    // --- PlanEntitlement value invariants (self-consistency) --------------------

    [Fact]
    public void A_flag_grant_carries_a_boolean_and_no_quota_limit()
    {
        var grant = StandardPlan().GrantFlag(FlagEntitlement(), false);

        Assert.NotNull(grant.FlagValue);
        Assert.Null(grant.QuotaLimit);
    }

    [Fact]
    public void A_quota_grant_carries_no_flag_value()
    {
        var grant = StandardPlan().GrantQuota(QuotaEntitlement(), 10);

        Assert.Null(grant.FlagValue);
    }

    // --- Log safety (threat T7) -------------------------------------------------

    [Fact]
    public void ToString_is_log_safe_and_excludes_the_display_metadata()
    {
        const string secretName = "INTERNAL-PLAN-NAME";
        var plan = PlanDefinition.Define("standard", secretName, "INTERNAL-DESC", _createdAt);
        plan.GrantQuota(QuotaEntitlement(), 1);

        var rendered = plan.ToString();

        Assert.Contains(plan.Id.ToString(), rendered);
        Assert.Contains("key=standard", rendered);
        Assert.Contains("grants=1", rendered);
        Assert.DoesNotContain(secretName, rendered);
    }
}
