using LiveCore.Api.Entitlements;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Domain invariant tests for the <see cref="QuotaDefinition"/> aggregate (CORE-ENTL-003). A quota definition says
/// HOW a numeric quota entitlement is measured (for which subject kind, in which unit); these tests pin that it can
/// only ever measure an active QUOTA entitlement (never a flag, never a retired entitlement — fail-closed), copies
/// the measured entitlement's stable key/id as a recorded fact, and soft-retires rather than mutates its identity.
/// All fixtures are generic Core keys (AGENTS.md).
/// </summary>
public sealed class QuotaDefinitionTests
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static EntitlementDefinition QuotaEntitlement(string key = "workspace.active.max")
        => EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Active workspace limit", null, _createdAt);

    private static EntitlementDefinition FlagEntitlement(string key = "ads.disabled")
        => EntitlementDefinition.Define(key, EntitlementValueKind.Flag, "Ad-free experience", null, _createdAt);

    [Fact]
    public void Define_copies_the_measured_entitlement_and_starts_active()
    {
        var entitlement = QuotaEntitlement();

        var quota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.User, QuotaUnit.Count, _createdAt);

        Assert.NotEqual(Guid.Empty, quota.Id);
        Assert.Equal(entitlement.Id, quota.EntitlementDefinitionId);
        Assert.Equal("workspace.active.max", quota.EntitlementKey);
        Assert.Equal(EntitlementSubjectType.User, quota.SubjectType);
        Assert.Equal(QuotaUnit.Count, quota.Unit);
        Assert.True(quota.IsActive);
        Assert.True(quota.IsForSubjectType(EntitlementSubjectType.User));
        Assert.False(quota.IsForSubjectType(EntitlementSubjectType.Workspace));
        Assert.Equal(_createdAt, quota.CreatedAt);
        Assert.Equal(_createdAt, quota.UpdatedAt);
    }

    [Fact]
    public void Define_rejects_a_flag_entitlement()
    {
        // A boolean capability has no numeric usage to measure, so it can never be the subject of a quota.
        var flag = FlagEntitlement();

        Assert.Throws<ArgumentException>(() =>
            QuotaDefinition.Define(flag, EntitlementSubjectType.User, QuotaUnit.Count, _createdAt));
    }

    [Fact]
    public void Define_rejects_a_retired_entitlement()
    {
        var retired = QuotaEntitlement();
        retired.Deactivate(_createdAt.AddHours(1));

        Assert.Throws<ArgumentException>(() =>
            QuotaDefinition.Define(retired, EntitlementSubjectType.User, QuotaUnit.Count, _createdAt));
    }

    [Fact]
    public void Define_rejects_a_null_entitlement()
        => Assert.Throws<ArgumentNullException>(() =>
            QuotaDefinition.Define(null!, EntitlementSubjectType.User, QuotaUnit.Count, _createdAt));

    [Fact]
    public void Define_rejects_an_undefined_subject_type()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuotaDefinition.Define(QuotaEntitlement(), (EntitlementSubjectType)999, QuotaUnit.Count, _createdAt));

    [Fact]
    public void Define_rejects_an_undefined_unit()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuotaDefinition.Define(QuotaEntitlement(), EntitlementSubjectType.User, (QuotaUnit)999, _createdAt));

    [Fact]
    public void Deactivate_and_reactivate_toggle_the_offered_flag()
    {
        var quota = QuotaDefinition.Define(QuotaEntitlement(), EntitlementSubjectType.Workspace, QuotaUnit.Count, _createdAt);

        quota.Deactivate(_createdAt.AddHours(1));
        Assert.False(quota.IsActive);

        quota.Reactivate(_createdAt.AddHours(2));
        Assert.True(quota.IsActive);
    }

    [Fact]
    public void ToString_is_identifier_only_and_log_safe()
    {
        var quota = QuotaDefinition.Define(QuotaEntitlement(), EntitlementSubjectType.User, QuotaUnit.Bytes, _createdAt);

        var text = quota.ToString();

        Assert.Contains("workspace.active.max", text, StringComparison.Ordinal);
        Assert.Contains("Bytes", text, StringComparison.Ordinal);
    }
}
