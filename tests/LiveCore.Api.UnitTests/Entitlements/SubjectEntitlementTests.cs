using LiveCore.Api.Entitlements;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Unit tests for the <see cref="SubjectEntitlement"/> invariants and its grant factories (CORE-ENTL-002, the
/// subject entitlement assignment and lookup story of the "Entitlements and Quotas" epic). A subject entitlement
/// is the server-side record that one generic subject holds a granted entitlement at a concrete value — the only
/// source of "User-visible premium state comes only from server entitlements"
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md).
///
/// The tests pin the fail-closed invariants — the value shape is fixed by the definition's kind, an inactive
/// definition can never be newly assigned, the subject pair and source-plan ids are bounded, and the lifecycle
/// (revoke/reinstate) and value re-grant behave — so a malformed or client-shaped premium grant can never be
/// constructed. All fixtures use generic Core keys only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class SubjectEntitlementTests
{
    private static readonly DateTimeOffset _grantedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _subjectId = Guid.CreateVersion7();
    private static readonly Guid _planId = Guid.CreateVersion7();

    private static EntitlementDefinition FlagDefinition(bool active = true)
    {
        var definition = EntitlementDefinition.Define(
            "ads.disabled", EntitlementValueKind.Flag, "Ad-free experience", null, _grantedAt);
        if (!active)
        {
            definition.Deactivate(_grantedAt);
        }

        return definition;
    }

    private static EntitlementDefinition QuotaDefinition(bool active = true)
    {
        var definition = EntitlementDefinition.Define(
            "workspace.active.max", EntitlementValueKind.Quota, "Active workspace limit", null, _grantedAt);
        if (!active)
        {
            definition.Deactivate(_grantedAt);
        }

        return definition;
    }

    // --- GrantFlag --------------------------------------------------------------

    [Fact]
    public void GrantFlag_builds_an_active_flag_assignment_copying_the_definition_key_and_id()
    {
        var definition = FlagDefinition();

        var assignment = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, definition, value: true, _planId, _grantedAt);

        Assert.NotEqual(Guid.Empty, assignment.Id);
        Assert.Equal(7, assignment.Id.Version); // UUIDv7, time-ordered (docs/10_DATABASE_SCHEMA.md)
        Assert.Equal(EntitlementSubjectType.User, assignment.SubjectType);
        Assert.Equal(_subjectId, assignment.SubjectId);
        Assert.Equal(definition.Id, assignment.EntitlementDefinitionId);
        Assert.Equal("ads.disabled", assignment.EntitlementKey); // denormalized from the definition
        Assert.Equal(EntitlementValueKind.Flag, assignment.ValueKind);
        Assert.True(assignment.FlagValue);
        Assert.Null(assignment.QuotaLimit);
        Assert.Equal(_planId, assignment.SourcePlanDefinitionId);
        Assert.True(assignment.IsActive);
        Assert.Equal(_grantedAt, assignment.GrantedAt);
        Assert.Equal(_grantedAt, assignment.UpdatedAt);
    }

    [Fact]
    public void GrantFlag_allows_a_direct_grant_with_no_source_plan()
    {
        var assignment = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(), value: false, sourcePlanDefinitionId: null, _grantedAt);

        Assert.Null(assignment.SourcePlanDefinitionId);
        Assert.False(assignment.FlagValue);
    }

    [Fact]
    public void GrantFlag_rejects_a_quota_definition()
        => Assert.Throws<ArgumentException>(() => SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, QuotaDefinition(), value: true, _planId, _grantedAt));

    [Fact]
    public void GrantFlag_rejects_an_inactive_definition()
        => Assert.Throws<ArgumentException>(() => SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(active: false), value: true, _planId, _grantedAt));

    [Fact]
    public void GrantFlag_rejects_an_empty_subject_id()
        => Assert.Throws<ArgumentException>(() => SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, Guid.Empty, FlagDefinition(), value: true, _planId, _grantedAt));

    [Fact]
    public void GrantFlag_rejects_an_empty_source_plan_id()
        => Assert.Throws<ArgumentException>(() => SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(), value: true, Guid.Empty, _grantedAt));

    [Fact]
    public void GrantFlag_rejects_a_null_definition()
        => Assert.Throws<ArgumentNullException>(() => SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, null!, value: true, _planId, _grantedAt));

    // --- GrantQuota -------------------------------------------------------------

    [Fact]
    public void GrantQuota_builds_an_active_quota_assignment_with_a_limit()
    {
        var definition = QuotaDefinition();

        var assignment = SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.Workspace, _subjectId, definition, limit: 4, _planId, _grantedAt);

        Assert.Equal(EntitlementSubjectType.Workspace, assignment.SubjectType);
        Assert.Equal(EntitlementValueKind.Quota, assignment.ValueKind);
        Assert.Equal(4, assignment.QuotaLimit);
        Assert.Null(assignment.FlagValue);
        Assert.False(assignment.IsUnlimitedQuota);
    }

    [Fact]
    public void GrantQuota_with_a_null_limit_is_an_unlimited_grant()
    {
        var assignment = SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, _subjectId, QuotaDefinition(), limit: null, _planId, _grantedAt);

        Assert.Null(assignment.QuotaLimit);
        Assert.True(assignment.IsUnlimitedQuota);
    }

    [Fact]
    public void GrantQuota_rejects_a_flag_definition()
        => Assert.Throws<ArgumentException>(() => SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(), limit: 1, _planId, _grantedAt));

    [Fact]
    public void GrantQuota_rejects_a_negative_limit()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, _subjectId, QuotaDefinition(), limit: -1, _planId, _grantedAt));

    [Fact]
    public void GrantQuota_rejects_an_inactive_definition()
        => Assert.Throws<ArgumentException>(() => SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, _subjectId, QuotaDefinition(active: false), limit: 1, _planId, _grantedAt));

    // --- Value re-grant (upgrade / re-grant after revocation) -------------------

    [Fact]
    public void ApplyFlagGrant_updates_the_value_source_and_reinstates()
    {
        var assignment = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(), value: false, sourcePlanDefinitionId: null, _grantedAt);
        assignment.Revoke(_grantedAt.AddHours(1));
        var newPlan = Guid.CreateVersion7();
        var updatedAt = _grantedAt.AddHours(2);

        assignment.ApplyFlagGrant(value: true, newPlan, updatedAt);

        Assert.True(assignment.FlagValue);
        Assert.Equal(newPlan, assignment.SourcePlanDefinitionId);
        Assert.True(assignment.IsActive); // reinstated
        Assert.Equal(updatedAt, assignment.UpdatedAt);
    }

    [Fact]
    public void ApplyFlagGrant_rejects_a_quota_assignment()
    {
        var assignment = SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, _subjectId, QuotaDefinition(), limit: 1, _planId, _grantedAt);

        Assert.Throws<InvalidOperationException>(() => assignment.ApplyFlagGrant(true, _planId, _grantedAt.AddHours(1)));
    }

    [Fact]
    public void ApplyQuotaGrant_updates_the_limit_source_and_reinstates()
    {
        var assignment = SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, _subjectId, QuotaDefinition(), limit: 1, _planId, _grantedAt);
        assignment.Revoke(_grantedAt.AddHours(1));
        var updatedAt = _grantedAt.AddHours(2);

        assignment.ApplyQuotaGrant(limit: null, sourcePlanDefinitionId: null, updatedAt);

        Assert.Null(assignment.QuotaLimit);
        Assert.True(assignment.IsUnlimitedQuota);
        Assert.Null(assignment.SourcePlanDefinitionId);
        Assert.True(assignment.IsActive);
        Assert.Equal(updatedAt, assignment.UpdatedAt);
    }

    [Fact]
    public void ApplyQuotaGrant_rejects_a_flag_assignment()
    {
        var assignment = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(), value: true, _planId, _grantedAt);

        Assert.Throws<InvalidOperationException>(() => assignment.ApplyQuotaGrant(1, _planId, _grantedAt.AddHours(1)));
    }

    [Fact]
    public void ApplyQuotaGrant_rejects_a_negative_limit()
    {
        var assignment = SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, _subjectId, QuotaDefinition(), limit: 1, _planId, _grantedAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => assignment.ApplyQuotaGrant(-5, _planId, _grantedAt.AddHours(1)));
    }

    // --- Lifecycle --------------------------------------------------------------

    [Fact]
    public void Revoke_then_reinstate_toggles_the_in_force_state()
    {
        var assignment = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(), value: true, _planId, _grantedAt);

        assignment.Revoke(_grantedAt.AddHours(1));
        Assert.False(assignment.IsActive);

        assignment.Reinstate(_grantedAt.AddHours(2));
        Assert.True(assignment.IsActive);
        Assert.Equal(_grantedAt.AddHours(2), assignment.UpdatedAt);
    }

    // --- Object-level identity --------------------------------------------------

    [Fact]
    public void IsForSubject_matches_only_the_exact_subject_pair()
    {
        var assignment = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(), value: true, _planId, _grantedAt);

        Assert.True(assignment.IsForSubject(EntitlementSubjectType.User, _subjectId));
        Assert.False(assignment.IsForSubject(EntitlementSubjectType.Workspace, _subjectId)); // different kind, same guid
        Assert.False(assignment.IsForSubject(EntitlementSubjectType.User, Guid.CreateVersion7()));
        Assert.False(assignment.IsForSubject(EntitlementSubjectType.User, Guid.Empty));
    }

    // --- UTC normalization ------------------------------------------------------

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localGrantedAt = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var assignment = SubjectEntitlement.GrantFlag(
            EntitlementSubjectType.User, _subjectId, FlagDefinition(), value: true, _planId, localGrantedAt);

        Assert.Equal(TimeSpan.Zero, assignment.GrantedAt.Offset);
        Assert.Equal(localGrantedAt.ToUniversalTime(), assignment.GrantedAt);
    }

    // --- Log safety (threat T7) -------------------------------------------------

    [Fact]
    public void ToString_is_log_safe_and_carries_identifiers_only()
    {
        var assignment = SubjectEntitlement.GrantQuota(
            EntitlementSubjectType.User, _subjectId, QuotaDefinition(), limit: 4, _planId, _grantedAt);

        var rendered = assignment.ToString();

        Assert.Contains(assignment.Id.ToString(), rendered);
        Assert.Contains("key=workspace.active.max", rendered);
        Assert.Contains("kind=Quota", rendered);
        Assert.Contains("quota=4", rendered);
        Assert.Contains("active=True", rendered);
    }
}
