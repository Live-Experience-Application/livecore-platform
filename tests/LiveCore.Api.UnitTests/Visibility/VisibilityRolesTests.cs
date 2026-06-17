// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="VisibilityRoles"/> (CORE-VIS-002) — the canonical classification of a
/// workspace role for the two CONTENT rows of docs/06_AUTHORIZATION_MATRIX.md. They pin the mapping
/// EXACTLY ("View host-only content" = yes for Owner/Admin/Host/CoHost; "View participant-visible
/// content" = "if visible" for Participant/Observer; Auditor audit-only on both) so a silent change
/// to either role set fails a test, confirm the two sets are DISJOINT, and confirm the deny-by-default
/// behavior for the audit role and an undefined role (threats T1/T5). <see cref="MembershipRole"/> is
/// non-linear, so the checks are exact set membership, never ordering comparisons.
/// </summary>
public sealed class VisibilityRolesTests
{
    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public void Host_content_roles_view_host_only_content(MembershipRole role)
    {
        Assert.True(VisibilityRoles.ViewsHostOnlyContent(role));
        Assert.False(VisibilityRoles.IsAudienceRole(role));
    }

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public void Audience_roles_are_audience_not_host(MembershipRole role)
    {
        Assert.True(VisibilityRoles.IsAudienceRole(role));
        Assert.False(VisibilityRoles.ViewsHostOnlyContent(role));
    }

    [Fact]
    public void Auditor_is_neither_host_nor_audience()
    {
        // The matrix grants Auditor only "audit-only" on both content rows — never a live content
        // grant, so it is in neither set and the policy denies it by default.
        Assert.False(VisibilityRoles.ViewsHostOnlyContent(MembershipRole.Auditor));
        Assert.False(VisibilityRoles.IsAudienceRole(MembershipRole.Auditor));
    }

    [Fact]
    public void Undefined_role_is_neither_host_nor_audience()
    {
        Assert.False(VisibilityRoles.ViewsHostOnlyContent((MembershipRole)999));
        Assert.False(VisibilityRoles.IsAudienceRole((MembershipRole)999));
    }

    [Fact]
    public void Exactly_the_four_host_roles_and_two_audience_roles_are_classified()
    {
        var hostRoles = Enum.GetValues<MembershipRole>()
            .Where(VisibilityRoles.ViewsHostOnlyContent)
            .ToArray();
        var audienceRoles = Enum.GetValues<MembershipRole>()
            .Where(VisibilityRoles.IsAudienceRole)
            .ToArray();

        Assert.Equal(
            new[] { MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Host, MembershipRole.CoHost },
            hostRoles);
        Assert.Equal(
            new[] { MembershipRole.Participant, MembershipRole.Observer },
            audienceRoles);
        // Disjoint: no role is both.
        Assert.Empty(hostRoles.Intersect(audienceRoles));
    }
}
