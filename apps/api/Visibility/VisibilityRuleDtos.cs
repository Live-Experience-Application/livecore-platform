// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// Request body for the visibility-rule create command (CORE-SVIS-005,
/// <c>POST /api/v1/sessions/{sessionId}/visibility-rules</c>, roles Owner/Admin/Host/CoHost). The session
/// is taken from the route path (it pins the workspace); the target organization is supplied as
/// <see cref="OrganizationSlug"/> and resolved by the tenant context resolver (token organization claim AND
/// persisted membership — defence in depth, threat T5), mirroring the reveal/hide commands exactly.
///
/// The body names the governed resource generically by its kind and id (<see cref="ResourceType"/> +
/// <see cref="ResourceId"/>), exactly as a visibility rule addresses its resource; it carries no vertical
/// vocabulary (docs/04_PRODUCT_BOUNDARIES.md). The same-workspace coupling of the referenced resource is
/// enforced SERVER-SIDE before the rule is created (the referenced resource must live in the session's
/// workspace).
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the session's workspace, used to resolve the tenant context
/// (the route carries no organization in its path).
/// </param>
/// <param name="ResourceType">
/// The kind of resource the rule governs — the name of a <see cref="VisibilityResourceType"/>
/// (Scene/ContentBlock/Entity). Parsed by name; a numeric or unknown value is a 400.
/// </param>
/// <param name="ResourceId">The surrogate id of the resource the rule governs (resolved within the workspace).</param>
/// <param name="Visibility">
/// The base audience visibility the rule assigns — the name of a <see cref="VisibilityState"/>
/// (Hidden/Visible). Parsed by name; a numeric or unknown value is a 400.
/// </param>
/// <param name="ParticipantId">
/// Optional target of a SELECTED-participant rule (CORE-VIS-005): when set, the rule's visibility applies
/// ONLY to that participant; when omitted/<see langword="null"/>, it applies to the whole audience. A
/// present-but-empty value is a 400; a set value must be a participant of the session's workspace (otherwise
/// hidden as 404).
/// </param>
/// <param name="ScheduledRevealAt">
/// Optional SCHEDULED-REVEAL time (UTC) for the rule (CORE-VSEAL-002): when set on a Hidden rule, the resource
/// stays hidden until this time and is then AUTOMATICALLY revealed by the worker's background sweep through the
/// central reveal engine (so the auto-reveal is gated through the Visibility engine and emits the normal session
/// events to exactly the authorized audience). When omitted/<see langword="null"/>, the rule has no schedule and
/// behaves exactly as before. A time in the past schedules an immediate auto-reveal on the next sweep.
/// </param>
public sealed record CreateVisibilityRuleRequest(
    string? OrganizationSlug,
    string? ResourceType,
    Guid ResourceId,
    string? Visibility,
    Guid? ParticipantId = null,
    DateTimeOffset? ScheduledRevealAt = null);

/// <summary>
/// Response body of a visibility rule (CORE-SVIS-005) — the shape returned by the create command and by the
/// list and by-id read routes (<c>GET /api/v1/sessions/{sessionId}/visibility-rules</c> and
/// <c>.../visibility-rules/{ruleId}</c>). It is a generic, product-neutral projection of a
/// <see cref="VisibilityRule"/>: the rule's id, the governed resource (kind + id + a denormalized,
/// audience-safe <see cref="ResourceLabel"/>), the base audience visibility state, the optional
/// selected-participant target and the server timestamps. Every field is an identifier, an enum name, an
/// audience-safe label or a timestamp — no resolved (host-only) content and no internal authorization
/// rationale (docs/08_API_CONTRACTS.md; threats T2/T7). The visibility rule is itself an authoring artifact
/// (a host configures it), so there is no host-vs-participant projection split — the routes are restricted to
/// the authoring roles, so a participant never receives this shape, but the label is nonetheless drawn from
/// the resource's AUDIENCE projection so it can never disclose host-only content even to an authoring caller.
///
/// THE AUDIENCE-SAFE RESOURCE LABEL (CORE-APROJ-004, raised by vertical adopter ARC-GAP-006). A
/// <see cref="VisibilityRule"/> names its governed resource only by (<see cref="ResourceType"/>,
/// <see cref="ResourceId"/>), so a host rendering a per-resource visibility matrix from
/// <c>listRules</c> previously had to read each resource individually to NAME its row.
/// <see cref="ResourceLabel"/> denormalizes that name onto the rule projection — a scene's title, an
/// entity's name, a content block's generic kind — so each row can be named from the rule list ALONE, with no
/// per-resource host read. It is resolved server-side through the Visibility-owned, same-workspace resource
/// port whose composition-root adapter consults the owning resource module's AUDIENCE projection (the central
/// security module may not reference Scenes/Content/Entities — CORE-ARCH-001), so the label is the resource's
/// audience-safe NAME, never its full content. A rule whose resource no longer resolves — a dangling rule
/// whose resource was deleted, or a resource that does not live in the rule's own workspace — degrades to a
/// <see langword="null"/> label WITHOUT error (the rule's identity and state still render).
/// </summary>
/// <param name="Id">The surrogate id of the visibility rule.</param>
/// <param name="ResourceType">The kind of resource the rule governs (the enum name).</param>
/// <param name="ResourceId">The surrogate id of the resource the rule governs.</param>
/// <param name="ResourceLabel">
/// The governed resource's denormalized, audience-safe label (a scene's title, an entity's name, a content
/// block's generic kind), or <see langword="null"/> when the resource no longer resolves in the rule's own
/// workspace (a dangling rule). Never the resource's full/host content (threats T2/T7).
/// </param>
/// <param name="Visibility">The base audience visibility state of the rule (the enum name, Hidden/Visible).</param>
/// <param name="ParticipantId">
/// The participant a selected-participant rule applies to, or <see langword="null"/> for an audience-wide
/// rule.
/// </param>
/// <param name="Locked">
/// Whether the rule is SEALED (locked) — the server-asserted authoring lock (CORE-VSEAL-001) that makes the
/// governed resource permanently-restricted: while <see langword="true"/>, a reveal/hide/change targeting the
/// rule is refused fail-closed (<c>409</c>). It is an ORTHOGONAL flag, NOT a third <see cref="Visibility"/>
/// state, so a consumer can render a locked presentation state bound to this server fact while the binary
/// Hidden/Visible state is unchanged. <see langword="false"/> for an unlocked rule (the default).
/// </param>
/// <param name="ScheduledRevealAt">
/// The optional SCHEDULED-REVEAL time (UTC) of the rule (CORE-VSEAL-002), or <see langword="null"/> when the
/// rule has no schedule. When set on a Hidden rule, the resource stays hidden until this time and is then
/// AUTOMATICALLY revealed by the worker's background sweep through the central reveal engine. It is projected so
/// a consumer can render a scheduled presentation state (the WHEN of visibility) — a server fact about when the
/// resource is/was scheduled to appear, never host content (threat T7).
/// </param>
/// <param name="CreatedAt">Server timestamp (UTC) at which the rule was first created.</param>
/// <param name="UpdatedAt">Server timestamp (UTC) at which the rule was last updated.</param>
public sealed record VisibilityRuleResponse(
    Guid Id,
    string ResourceType,
    Guid ResourceId,
    string? ResourceLabel,
    string Visibility,
    Guid? ParticipantId,
    bool Locked,
    DateTimeOffset? ScheduledRevealAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="VisibilityRule"/> into its response DTO, denormalizing the governed resource's
    /// audience-safe label (<paramref name="resourceLabel"/>, resolved by the endpoint through the
    /// Visibility-owned <see cref="IVisibleResourceAudienceProjector"/> port, or <see langword="null"/> when
    /// the resource no longer resolves) onto the row and carrying the rule's sealed/locked flag
    /// (CORE-VSEAL-001) and its optional scheduled-reveal time (CORE-VSEAL-002). The factory is shared by the
    /// create, list, by-id read and lock/unlock routes so the shapes can never diverge.
    /// </summary>
    public static VisibilityRuleResponse From(VisibilityRule rule, string? resourceLabel)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new VisibilityRuleResponse(
            rule.Id,
            rule.ResourceType.ToString(),
            rule.ResourceId,
            resourceLabel,
            rule.Visibility.ToString(),
            rule.TargetParticipantId,
            rule.Locked,
            rule.ScheduledRevealAt,
            rule.CreatedAt,
            rule.UpdatedAt);
    }
}
