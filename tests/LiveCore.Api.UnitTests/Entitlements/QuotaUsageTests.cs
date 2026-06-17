// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Domain invariant tests for the <see cref="QuotaUsage"/> aggregate (CORE-ENTL-003). A usage row records how much
/// of a quota one generic subject currently consumes — the "used" half of the quota-status calculation; these
/// tests pin that it starts at zero, records only non-negative amounts, copies the measured quota's stable key as a
/// recorded fact, and is keyed strictly by the (subject type, subject id) pair. All fixtures are generic Core keys
/// (AGENTS.md).
/// </summary>
public sealed class QuotaUsageTests
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static QuotaDefinition Quota(string key = "session.active.max")
    {
        var entitlement = EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Active session limit", null, _createdAt);
        return QuotaDefinition.Define(entitlement, EntitlementSubjectType.User, QuotaUnit.Count, _createdAt);
    }

    [Fact]
    public void Start_records_zero_usage_and_copies_the_measured_quota()
    {
        var quota = Quota();
        var subjectId = Guid.CreateVersion7();

        var usage = QuotaUsage.Start(EntitlementSubjectType.User, subjectId, quota, _createdAt);

        Assert.NotEqual(Guid.Empty, usage.Id);
        Assert.Equal(EntitlementSubjectType.User, usage.SubjectType);
        Assert.Equal(subjectId, usage.SubjectId);
        Assert.Equal(quota.Id, usage.QuotaDefinitionId);
        Assert.Equal("session.active.max", usage.EntitlementKey);
        Assert.Equal(0, usage.UsedAmount);
        Assert.True(usage.IsForSubject(EntitlementSubjectType.User, subjectId));
        Assert.False(usage.IsForSubject(EntitlementSubjectType.Workspace, subjectId));
    }

    [Fact]
    public void Record_sets_the_absolute_amount()
    {
        var usage = QuotaUsage.Start(EntitlementSubjectType.Workspace, Guid.CreateVersion7(), Quota(), _createdAt);

        usage.Record(7, _createdAt.AddMinutes(1));
        Assert.Equal(7, usage.UsedAmount);

        // Absolute, not additive: a later record replaces the amount.
        usage.Record(3, _createdAt.AddMinutes(2));
        Assert.Equal(3, usage.UsedAmount);
    }

    [Fact]
    public void Record_rejects_a_negative_amount()
    {
        var usage = QuotaUsage.Start(EntitlementSubjectType.User, Guid.CreateVersion7(), Quota(), _createdAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => usage.Record(-1, _createdAt.AddMinutes(1)));
    }

    [Fact]
    public void Start_rejects_an_empty_subject_id()
        => Assert.Throws<ArgumentException>(() =>
            QuotaUsage.Start(EntitlementSubjectType.User, Guid.Empty, Quota(), _createdAt));

    [Fact]
    public void Start_rejects_a_null_definition()
        => Assert.Throws<ArgumentNullException>(() =>
            QuotaUsage.Start(EntitlementSubjectType.User, Guid.CreateVersion7(), null!, _createdAt));
}
