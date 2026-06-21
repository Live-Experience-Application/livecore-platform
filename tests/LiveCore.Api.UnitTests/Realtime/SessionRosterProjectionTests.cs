// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionRosterProjection"/> (CORE-PRS-002) — the pure, role-based projector that selects
/// which of the two distinct roster view shapes a caller receives and stamps each participant's presence. The
/// host-content roles (Owner/Admin/Host/CoHost) receive the full <see cref="SessionRosterView"/> WITH the
/// host-only participant user link; every other role — the audience roles Participant/Observer, the Auditor and
/// any UNDEFINED value — falls closed to the host-only-field-stripped <see cref="ParticipantRosterView"/>
/// (threat T2 visibility leak in docs/07_SECURITY_THREAT_MODEL.md). The partition reuses the central
/// <c>VisibilityRoles</c> classification, so it is exact and never an ordering comparison.
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class SessionRosterProjectionTests
{
    private static readonly Guid _org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _workspace = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _session = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset _now = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The roles that receive the FULL host roster (VisibilityRoles.ViewsHostOnlyContent).</summary>
    public static TheoryData<MembershipRole> FullViewRoles =>
        [MembershipRole.Owner, MembershipRole.Admin, MembershipRole.Host, MembershipRole.CoHost];

    /// <summary>The roles that fall closed to the stripped roster (audience roles + the audit role).</summary>
    public static TheoryData<MembershipRole> StrippedViewRoles =>
        [MembershipRole.Participant, MembershipRole.Observer, MembershipRole.Auditor];

    [Theory]
    [MemberData(nameof(FullViewRoles))]
    public void A_host_role_gets_the_full_view_with_the_user_link_and_presence(MembershipRole role)
    {
        var linked = Participant.Create(_org, _workspace, Guid.NewGuid(), "Ada", _now);
        var anonymous = Participant.Create(_org, _workspace, userProfileId: null, "Guest", _now);
        var present = new HashSet<Guid> { linked.Id };

        var result = SessionRosterProjection.Project(
            _session, [linked, anonymous], present, role, callerParticipantId: linked.Id);

        var view = Assert.IsType<SessionRosterView>(result);
        Assert.Equal(_session, view.SessionId);
        var ada = view.Participants.Single(p => p.ParticipantId == linked.Id);
        Assert.Equal("Ada", ada.DisplayName);
        Assert.Equal(linked.UserProfileId, ada.UserProfileId); // host sees the user link
        Assert.True(ada.Present);
        var guest = view.Participants.Single(p => p.ParticipantId == anonymous.Id);
        Assert.Null(guest.UserProfileId); // an anonymous participant has no link, even in the host view
        Assert.False(guest.Present);
    }

    [Theory]
    [MemberData(nameof(StrippedViewRoles))]
    public void A_non_host_role_gets_the_stripped_view_without_the_user_link(MembershipRole role)
    {
        var linked = Participant.Create(_org, _workspace, Guid.NewGuid(), "Ada", _now);
        var present = new HashSet<Guid> { linked.Id };

        var result = SessionRosterProjection.Project(
            _session, [linked], present, role, callerParticipantId: null);

        // A ParticipantRosterParticipant has no user-link property at all — the host-only field is absent by type.
        var view = Assert.IsType<ParticipantRosterView>(result);
        Assert.Equal(_session, view.SessionId);
        var ada = Assert.Single(view.Participants);
        Assert.Equal("Ada", ada.DisplayName);
        Assert.True(ada.Present); // presence is participant-safe
    }

    [Fact]
    public void The_audience_view_marks_only_the_callers_own_entry_isSelf()
    {
        // CORE-PSELF-001: isSelf is server-computed per caller from the caller's OWN participant id, so it is
        // true for exactly the caller's entry and false for every other participant — leaking no other
        // participant's identity.
        var caller = Participant.Create(_org, _workspace, Guid.NewGuid(), "Ada", _now);
        var other = Participant.Create(_org, _workspace, Guid.NewGuid(), "Grace", _now);

        var result = SessionRosterProjection.Project(
            _session, [caller, other], new HashSet<Guid>(), MembershipRole.Participant, callerParticipantId: caller.Id);

        var view = Assert.IsType<ParticipantRosterView>(result);
        Assert.True(view.Participants.Single(p => p.ParticipantId == caller.Id).IsSelf);
        Assert.False(view.Participants.Single(p => p.ParticipantId == other.Id).IsSelf);
    }

    [Fact]
    public void The_audience_view_marks_no_entry_isSelf_when_the_caller_is_not_a_participant()
    {
        // A caller with no participant record of its own (a null caller participant id) sees isSelf false for
        // every entry — fail-closed, never accidentally claiming another participant as "self".
        var first = Participant.Create(_org, _workspace, Guid.NewGuid(), "Ada", _now);
        var second = Participant.Create(_org, _workspace, Guid.NewGuid(), "Grace", _now);

        var result = SessionRosterProjection.Project(
            _session, [first, second], new HashSet<Guid>(), MembershipRole.Observer, callerParticipantId: null);

        var view = Assert.IsType<ParticipantRosterView>(result);
        Assert.All(view.Participants, p => Assert.False(p.IsSelf));
    }

    [Fact]
    public void An_undefined_role_falls_closed_to_the_stripped_view()
    {
        // A value outside the enum must never receive the broader host view that carries the user link.
        var participant = Participant.Create(_org, _workspace, Guid.NewGuid(), "Ada", _now);

        var result = SessionRosterProjection.Project(
            _session, [participant], new HashSet<Guid>(), (MembershipRole)999, callerParticipantId: null);

        Assert.IsType<ParticipantRosterView>(result);
    }

    [Fact]
    public void An_empty_roster_projects_an_empty_participant_list()
    {
        var hostResult = SessionRosterProjection.Project(
            _session, [], new HashSet<Guid>(), MembershipRole.Host, callerParticipantId: null);
        var audienceResult = SessionRosterProjection.Project(
            _session, [], new HashSet<Guid>(), MembershipRole.Participant, callerParticipantId: null);

        Assert.Empty(Assert.IsType<SessionRosterView>(hostResult).Participants);
        Assert.Empty(Assert.IsType<ParticipantRosterView>(audienceResult).Participants);
    }
}
