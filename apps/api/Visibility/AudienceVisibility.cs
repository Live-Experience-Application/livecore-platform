// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The resolution of an AUDIENCE-WIDE event's recipients against a resource's visibility rules
/// (CORE-PERF-001), computed from a SINGLE <see cref="IVisibilityRuleRepository.ListByResourceAsync"/>
/// lookup instead of one query per participant. It answers the two questions the Realtime recipient
/// resolver needs for an audience-wide event in one pass:
/// <list type="bullet">
///   <item><see cref="AudienceVisible"/> — whether the WHOLE audience may see the resource (an
///   AUDIENCE-WIDE visible rule exists). When true the event is delivered to the shared session-audience
///   group with one backplane publish (<see cref="LiveCore.Api.Realtime.RealtimeGroups.SessionAudience"/>),
///   so delivery no longer grows linearly with audience size.</item>
///   <item><see cref="SelectedVisibleParticipantIds"/> — the participants who may see the resource ONLY
///   through a visible rule SCOPED TO THEM (a selected-participant reveal on the same resource), with NO
///   audience-wide visible rule. They are entitled to an audience-wide event ABOUT that resource even when
///   the audience-at-large is not, so the resolver delivers it to exactly their own participant groups —
///   the same per-participant outcome the old fan-out produced, but derived in memory from the one lookup,
///   never re-queried per participant. When <see cref="AudienceVisible"/> is true these participants are
///   already covered by the shared audience group, so this set is consulted only for the audience-hidden
///   case.</item>
/// </list>
///
/// Both fields come from the SAME rules the central <see cref="VisibilityPolicy"/> evaluates (the rule
/// predicates live on the <see cref="VisibilityRule"/> aggregate), so the collapsed audience decision can
/// never diverge from the per-resource visibility decision — visibility stays decided in one place
/// (docs/05_MODULE_CONTRACTS.md). Fail-closed: a malformed/unknown subject yields <see cref="None"/> (no
/// audience, no participants), so a malformed event never widens the audience (threats T1/T3).
/// </summary>
/// <param name="AudienceVisible">Whether an audience-wide visible rule makes the resource visible to the whole audience.</param>
/// <param name="SelectedVisibleParticipantIds">
/// The participants made visible ONLY by a participant-scoped visible rule (no display name or content —
/// surrogate ids only; threat T7). Never null.
/// </param>
internal readonly record struct AudienceVisibility(
    bool AudienceVisible,
    IReadOnlyList<Guid> SelectedVisibleParticipantIds)
{
    /// <summary>The fail-closed empty resolution: the audience may not see it and no participant is individually entitled.</summary>
    public static AudienceVisibility None { get; } = new(AudienceVisible: false, []);
}
