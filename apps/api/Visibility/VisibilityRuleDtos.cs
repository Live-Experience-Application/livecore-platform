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
public sealed record CreateVisibilityRuleRequest(
    string? OrganizationSlug,
    string? ResourceType,
    Guid ResourceId,
    string? Visibility,
    Guid? ParticipantId = null);

/// <summary>
/// Response body of a visibility rule (CORE-SVIS-005) — the shape returned by the create command and by the
/// list and by-id read routes (<c>GET /api/v1/sessions/{sessionId}/visibility-rules</c> and
/// <c>.../visibility-rules/{ruleId}</c>). It is a generic, product-neutral projection of a
/// <see cref="VisibilityRule"/>: the rule's id, the governed resource (kind + id), the base audience
/// visibility state, the optional selected-participant target and the server timestamps. Every field is an
/// identifier, an enum name or a timestamp — no resolved content and no internal authorization rationale
/// (docs/08_API_CONTRACTS.md; threat T7). The visibility rule is itself an authoring artifact (a host
/// configures it), so there is no host-vs-participant projection split — the routes are restricted to the
/// authoring roles, so a participant never receives this shape.
/// </summary>
/// <param name="Id">The surrogate id of the visibility rule.</param>
/// <param name="ResourceType">The kind of resource the rule governs (the enum name).</param>
/// <param name="ResourceId">The surrogate id of the resource the rule governs.</param>
/// <param name="Visibility">The base audience visibility state of the rule (the enum name, Hidden/Visible).</param>
/// <param name="ParticipantId">
/// The participant a selected-participant rule applies to, or <see langword="null"/> for an audience-wide
/// rule.
/// </param>
/// <param name="CreatedAt">Server timestamp (UTC) at which the rule was first created.</param>
/// <param name="UpdatedAt">Server timestamp (UTC) at which the rule was last updated.</param>
public sealed record VisibilityRuleResponse(
    Guid Id,
    string ResourceType,
    Guid ResourceId,
    string Visibility,
    Guid? ParticipantId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Projects a <see cref="VisibilityRule"/> into its response DTO.</summary>
    public static VisibilityRuleResponse From(VisibilityRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new VisibilityRuleResponse(
            rule.Id,
            rule.ResourceType.ToString(),
            rule.ResourceId,
            rule.Visibility.ToString(),
            rule.TargetParticipantId,
            rule.CreatedAt,
            rule.UpdatedAt);
    }
}
