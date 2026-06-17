// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Organizations;

/// <summary>
/// Unit tests for the <see cref="OrganizationMember"/> invariants and the
/// <see cref="MembershipRole"/> set (CORE-ID-004): a membership links one
/// subject to one organization with one generic role; it is tenant-scoped and
/// grants standing only in the organization it names; the role is matched
/// exactly (the authorization matrix is non-linear, so there is no role
/// "floor") (threats T5/T7 in docs/07_SECURITY_THREAT_MODEL.md;
/// docs/06_AUTHORIZATION_MATRIX.md). ToString stays log-safe (threat T7).
/// </summary>
public class OrganizationMemberTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _subjectId = Guid.CreateVersion7();
    private static readonly Guid _foreignSubjectId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_copies_organization_subject_and_role()
    {
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Admin, _createdAt);

        Assert.NotEqual(Guid.Empty, member.Id);
        Assert.Equal(_organizationId, member.OrganizationId);
        Assert.Equal(_subjectId, member.UserProfileId);
        Assert.Equal(MembershipRole.Admin, member.Role);
        Assert.Equal(_createdAt, member.CreatedAt);
        Assert.Equal(_createdAt, member.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Owner, _createdAt);
        var second = OrganizationMember.Create(_organizationId, _foreignSubjectId, MembershipRole.Owner, _createdAt);

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.FromHours(2));

        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Host, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, member.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), member.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => OrganizationMember.Create(Guid.Empty, _subjectId, MembershipRole.Participant, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_subject_id()
        => Assert.Throws<ArgumentException>(
            () => OrganizationMember.Create(_organizationId, Guid.Empty, MembershipRole.Participant, _createdAt));

    [Fact]
    public void Create_rejects_an_undefined_role()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => OrganizationMember.Create(_organizationId, _subjectId, (MembershipRole)999, _createdAt));

    [Fact]
    public void Membership_belongs_only_to_its_own_organization()
    {
        // Negative tenant case (threat T5): a membership grants standing only
        // in the organization it names. The foreign organization id, even
        // though it is a real organization, does not match.
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Owner, _createdAt);

        Assert.True(member.BelongsToOrganization(_organizationId));
        Assert.False(member.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(member.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void Membership_belongs_only_to_its_own_subject()
    {
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Owner, _createdAt);

        Assert.True(member.BelongsToSubject(_subjectId));
        Assert.False(member.BelongsToSubject(_foreignSubjectId));
        Assert.False(member.BelongsToSubject(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_both_the_exact_organization_and_subject()
    {
        // A membership in organization A for subject S is NOT the membership
        // for the same subject in organization B, nor for another subject in
        // organization A (threat T5).
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Admin, _createdAt);

        Assert.True(member.Identifies(_organizationId, _subjectId));
        Assert.False(member.Identifies(_foreignOrganizationId, _subjectId));
        Assert.False(member.Identifies(_organizationId, _foreignSubjectId));
        Assert.False(member.Identifies(_foreignOrganizationId, _foreignSubjectId));
    }

    [Fact]
    public void HasRole_is_true_only_for_the_exact_role()
    {
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Admin, _createdAt);

        Assert.True(member.HasRole(MembershipRole.Admin));
        // Negative authorization case: a different role never matches, in
        // either direction. The roles are not linearly ordered, so neither a
        // "higher" nor a "lower" role satisfies an Admin membership check
        // (docs/06_AUTHORIZATION_MATRIX.md).
        Assert.False(member.HasRole(MembershipRole.Owner));
        Assert.False(member.HasRole(MembershipRole.Participant));
        Assert.False(member.HasRole(MembershipRole.Auditor));
    }

    [Fact]
    public void HasRole_does_not_treat_an_auditor_as_below_a_participant()
    {
        // Guards the corrected non-linear model: an auditor (who may view the
        // audit log and metadata a participant may not) is not modelled as a
        // weaker participant. Exact-role matching keeps the two distinct rather
        // than ranking one under the other (docs/06_AUTHORIZATION_MATRIX.md).
        var auditor = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Auditor, _createdAt);
        var participant = OrganizationMember.Create(
            _organizationId, _foreignSubjectId, MembershipRole.Participant, _createdAt);

        Assert.True(auditor.HasRole(MembershipRole.Auditor));
        Assert.False(auditor.HasRole(MembershipRole.Participant));
        Assert.True(participant.HasRole(MembershipRole.Participant));
        Assert.False(participant.HasRole(MembershipRole.Auditor));
    }

    [Fact]
    public void HasRole_rejects_an_undefined_role()
    {
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Owner, _createdAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => member.HasRole((MembershipRole)999));
    }

    [Fact]
    public void ChangeRole_updates_the_role_and_timestamp_but_not_the_tenant_or_subject()
    {
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Participant, _createdAt);

        member.ChangeRole(MembershipRole.Host, _updatedAt);

        Assert.Equal(MembershipRole.Host, member.Role);
        Assert.Equal(_updatedAt, member.UpdatedAt);
        Assert.Equal(_createdAt, member.CreatedAt);
        // The tenant boundary and the subject are immutable: re-roling never
        // moves the membership (threat T5).
        Assert.Equal(_organizationId, member.OrganizationId);
        Assert.Equal(_subjectId, member.UserProfileId);
    }

    [Fact]
    public void ChangeRole_rejects_an_undefined_role_and_leaves_the_membership_untouched()
    {
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Host, _createdAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => member.ChangeRole((MembershipRole)999, _updatedAt));

        Assert.Equal(MembershipRole.Host, member.Role);
        Assert.Equal(_createdAt, member.UpdatedAt);
    }

    [Fact]
    public void ToString_exposes_identifiers_and_role_but_no_free_form_content()
    {
        // Log-safety (threat T7): structured logs carry identifiers and the
        // authorization role, never free-form content.
        var member = OrganizationMember.Create(_organizationId, _subjectId, MembershipRole.Owner, _createdAt);

        var text = member.ToString();

        Assert.Contains(member.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_subjectId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("Owner", text, StringComparison.Ordinal);
    }
}
