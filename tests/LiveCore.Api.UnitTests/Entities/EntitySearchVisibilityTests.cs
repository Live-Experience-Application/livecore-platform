// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Tests for <see cref="EntitySearchVisibility"/> (CORE-ENT-005, extended by CORE-API-006) — the
/// role split that gates entity search into the host, audience-participant and fail-closed views.
/// They pin the mapping EXACTLY to the "View host-only content" row of
/// docs/06_AUTHORIZATION_MATRIX.md (Owner/Admin/Host/CoHost = yes; Participant/Observer = audience;
/// Auditor = audit-only, in neither set) so a silent change to either role set fails a test, and they
/// confirm the deny-by-default behavior for an undefined role (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; <see cref="MembershipRole"/> is non-linear, so the check is
/// exact set membership, never an ordering comparison).
/// </summary>
public sealed class EntitySearchVisibilityTests
{
    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    public void Host_capable_roles_view_host_only_content(MembershipRole role)
        => Assert.True(EntitySearchVisibility.ViewsHostOnlyContent(role));

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public void Audience_and_audit_roles_do_not_view_host_only_content(MembershipRole role)
        => Assert.False(EntitySearchVisibility.ViewsHostOnlyContent(role));

    [Theory]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    public void Audience_roles_view_the_audience_filtered_content(MembershipRole role)
        => Assert.True(EntitySearchVisibility.ViewsAudienceFilteredContent(role));

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Auditor)]
    [InlineData((MembershipRole)999)]
    public void Non_audience_roles_do_not_view_the_audience_filtered_content(MembershipRole role)
        => Assert.False(EntitySearchVisibility.ViewsAudienceFilteredContent(role));

    [Fact]
    public void The_host_and_audience_views_are_disjoint()
    {
        // No role is BOTH a host-content role and an audience role: the two predicates partition the
        // content rows of docs/06, so a caller takes exactly one path (or fails closed).
        foreach (var role in Enum.GetValues<MembershipRole>())
        {
            Assert.False(
                EntitySearchVisibility.ViewsHostOnlyContent(role)
                    && EntitySearchVisibility.ViewsAudienceFilteredContent(role));
        }
    }

    [Fact]
    public void Auditor_is_excluded_unlike_the_scene_metadata_projection()
    {
        // The matrix grants Auditor "View workspace metadata" = yes (so SceneProjection gives it the
        // host SHAPE) but "View host-only content" = audit-only — an entity IS content, so the
        // auditor does NOT get the host-only-content view here. This guards the deliberate
        // distinction between metadata projection and content search.
        Assert.False(EntitySearchVisibility.ViewsHostOnlyContent(MembershipRole.Auditor));
    }

    [Fact]
    public void Undefined_role_fails_closed_to_audience()
    {
        // A value outside the defined enum (e.g. smuggled in by a cast) must never be granted the
        // host-only-content view — deny-by-default.
        Assert.False(EntitySearchVisibility.ViewsHostOnlyContent((MembershipRole)999));
    }

    [Fact]
    public void Every_defined_role_is_classified_and_only_the_four_host_roles_qualify()
    {
        // Whole-enum sweep: exactly Owner/Admin/Host/CoHost qualify; the remaining defined roles do
        // not. This catches a new role being silently added to (or removed from) the host set.
        var hostRoles = Enum.GetValues<MembershipRole>()
            .Where(EntitySearchVisibility.ViewsHostOnlyContent)
            .ToArray();

        Assert.Equal(
            new[] { MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Host, MembershipRole.CoHost },
            hostRoles);
    }
}
