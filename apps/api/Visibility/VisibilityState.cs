// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The audience visibility state a <see cref="VisibilityRule"/> assigns to its resource
/// (CORE-VIS-001). It is the generic, server-side answer to "may the audience see this resource?":
/// host-prepared resources are <see cref="Hidden"/> from the audience until a host reveals them,
/// at which point the rule's state becomes <see cref="Visible"/> (docs/06_AUTHORIZATION_MATRIX.md:
/// "View host-only content" vs "View participant-visible content"; docs/03_DOMAIN_LANGUAGE.md:
/// Reveal = "Command/action that changes visibility or sends content").
///
/// This is the BASE, audience-wide visibility state. Whether the resource is visible to the WHOLE
/// audience is all this binary state expresses; sending a resource privately to a SELECTED subset of
/// participants is a later story (selected-participant reveal, CORE-VIS-005) and is deliberately NOT
/// modelled as an additional state here. Evaluating a viewer's effective access from these rules
/// (<c>CanViewResource</c>) is the policy story CORE-VIS-002, and the reveal COMMAND that flips the
/// state with authorization, idempotency and an append-only event is CORE-VIS-004 — none of which
/// this enum or the CORE-VIS-001 aggregate implements.
///
/// The state is persisted by its stable NAME (not its numeric value), exactly like
/// <c>SessionStatus</c> and <c>ContentBlockType</c>, so the integers below are only in-memory
/// storage discriminators and carry no ordering meaning; they must not be compared with &gt;/&lt;.
/// </summary>
public enum VisibilityState
{
    /// <summary>
    /// The resource is hidden from the audience: it is host-only content and not part of any
    /// participant-visible projection. This is the default state of host-prepared content before a
    /// reveal (docs/06_AUTHORIZATION_MATRIX.md "View host-only content").
    /// </summary>
    Hidden = 1,

    /// <summary>
    /// The resource is visible to the audience: a host has revealed it, so it is part of the
    /// participant-visible projection (docs/06_AUTHORIZATION_MATRIX.md "View participant-visible
    /// content"). Restricting visibility to a selected subset of participants instead of the whole
    /// audience is the later selected-participant reveal (CORE-VIS-005).
    /// </summary>
    Visible = 2,
}
