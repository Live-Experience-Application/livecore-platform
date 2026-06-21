// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The audience-safe marker that distinguishes HOW a resource is visible to a participant in their
/// visible feed (CORE-APROJ-002): whether it was revealed to the WHOLE audience or only to this
/// SELECTED participant. It is the participant-facing companion of the rule's
/// <see cref="VisibilityRule.IsAudienceWide"/> flag, computed by the central
/// <see cref="VisibilityPolicy"/> from the rules that grant the participant visibility.
///
/// It is audience-SAFE and product-neutral: for a participant's OWN feed (or a Host/CoHost preview of
/// it) the marker describes only THIS participant's own view — an audience-wide reveal versus a private
/// reveal scoped to exactly them — and never names or counts the OTHER participants a private reveal
/// might target, so it leaks no host-only or cross-participant detail (threats T2/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). It is serialized by its stable NAME, never a numeric value
/// (mirroring how every other Core enum reaches the wire).
/// </summary>
internal enum VisibleResourceRevealScope
{
    /// <summary>
    /// The resource is visible to the participant because it was revealed to the WHOLE audience (an
    /// audience-wide visible rule applies — <see cref="VisibilityRule.IsAudienceWide"/>). The participant
    /// sees it exactly as every other audience member does.
    /// </summary>
    AudienceWide,

    /// <summary>
    /// The resource is visible to the participant ONLY through a reveal scoped to exactly them (a
    /// selected-participant private reveal; <see cref="VisibilityRule.TargetsParticipant"/>), with no
    /// audience-wide visible rule. A non-selected participant does not see it (the selected-participant
    /// guarantee; threat T5).
    /// </summary>
    SelectedParticipant,
}
