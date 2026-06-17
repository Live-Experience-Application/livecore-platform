// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;

namespace LiveCore.Api.Visibility;

/// <summary>
/// The Visibility module's server-side access policy (CORE-VIS-002), providing the
/// <c>CanViewResource</c> operation docs/05_MODULE_CONTRACTS.md assigns to the Visibility module —
/// THE central security module, where visibility decisions live and are not "duplicate[d] elsewhere".
/// It is a plain, unit-testable decision service over <see cref="IVisibilityRuleRepository"/> that
/// takes explicit inputs, exactly like <c>EntitySearchService</c> (CORE-ENT-005) and
/// <c>SessionParticipantJoinService</c> (CORE-SES-003); resolving the "current" organization,
/// workspace and the caller's role from a request is the tenant context resolver and a later endpoint
/// story (csv/api_routes.csv defines no <c>CanViewResource</c> route, so this story adds NO HTTP
/// endpoint).
///
/// The decision answers "may this viewer see this resource?" from the caller's WORKSPACE role and the
/// resource's visibility rules (the CORE-VIS-001 <see cref="VisibilityRule"/> rows), STRICTLY per the
/// two content rows of docs/06_AUTHORIZATION_MATRIX.md:
/// <list type="bullet">
///   <item>HOST-CONTENT roles (Owner/Admin/Host/CoHost — "View host-only content" = <c>yes</c>,
///   <see cref="VisibilityRoles.ViewsHostOnlyContent"/>) may see the resource whether it is hidden or
///   visible. The decision short-circuits to ALLOW BEFORE any database read, so a host caller never
///   triggers a rule lookup.</item>
///   <item>AUDIENCE roles (Participant/Observer — "View participant-visible content" = "if visible",
///   <see cref="VisibilityRoles.IsAudienceRole"/>) may see the resource ONLY when a visibility rule
///   makes it visible to the audience. The policy reads the resource's rules
///   (<see cref="IVisibilityRuleRepository.ListByResourceAsync"/>, tenant- and workspace-scoped) and
///   ALLOWS iff at least one rule is <see cref="VisibilityState.Visible"/>; otherwise it DENIES
///   (the resource is host-only content the audience may not see). Because the index is non-unique a
///   resource may carry several rules; any visible rule grants visibility.</item>
///   <item>EVERY OTHER role — the audit role Auditor (audit-only on both content rows, an explicit
///   audit-trail concern, never a live content grant) and any undefined enum value — is DENIED by
///   default (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). The audience read is not even run
///   for these, so no rule existence can leak.</item>
/// </list>
///
/// Boundary order (docs/06 authorization principles): the organization boundary is checked before the
/// workspace boundary, the workspace before the SESSION, and the session before resource-level
/// visibility — the rule lookup leads with <c>organization_id</c> then <c>workspace_id</c> then
/// <c>session_id</c>, so a rule in another tenant, workspace or session can never make a resource visible
/// here (threat T5/T3). EVERY visibility decision is SESSION-SCOPED (CORE-SVIS-001, completed by
/// CORE-SVIS-004): a reveal belongs to the session it was made in, so a session-agnostic, workspace-wide
/// decision is not representable here — every caller (the reveal command, the participant feed, the
/// realtime recipient gate, replay, asset download and entity search) passes a <c>sessionId</c>, and the
/// workspace-wide overloads that once spanned sessions have been REMOVED so a cross-session leak cannot be
/// reintroduced. The caller is responsible for having resolved the workspace <see cref="MembershipRole"/>
/// from a verified membership (the tenant context resolver); this policy trusts the passed role exactly as
/// <c>EntitySearchService</c> does and decides only the content-visibility question.
///
/// SCOPE: this story implements only <c>CanViewResource</c>. Computing a participant's full visible
/// set (<c>GetVisibleResourcesForParticipant</c> — the audience computation the CORE-SES-005 and
/// CORE-ENT-005 skeletons left empty) and preview-as-participant (<c>PreviewVisibilityForHost</c>)
/// are the later CORE-VIS-003 stories; the reveal COMMAND with idempotency and an append-only event
/// is CORE-VIS-004; selected-participant reveal is CORE-VIS-005; audit records are CORE-VIS-006. None
/// of those are built here.
/// </summary>
internal sealed class VisibilityPolicy
{
    private readonly IVisibilityRuleRepository _rules;

    public VisibilityPolicy(IVisibilityRuleRepository rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>
    /// Decides whether the given workspace role may view the given resource IN THE GIVEN SESSION
    /// (CORE-SVIS-001), applying the host-content vs audience-visible rules of
    /// docs/06_AUTHORIZATION_MATRIX.md over the resource's SESSION-SCOPED visibility rules.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the resource belongs to.</param>
    /// <param name="sessionId">The session whose reveal rules the decision is bounded by (checked last).</param>
    /// <param name="viewerRole">The caller's role in <paramref name="workspaceId"/>.</param>
    /// <param name="resourceType">The kind of resource being viewed.</param>
    /// <param name="resourceId">The surrogate id of the resource being viewed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An ALLOW decision (with the granting reason) for a host role, or for an audience role when a
    /// visibility rule of this session makes the resource visible; otherwise a DENY decision (with the
    /// denying reason).
    /// </returns>
    /// <exception cref="ArgumentException">The organization id, workspace id, session id or resource id is empty.</exception>
    public Task<ResourceVisibilityDecision> CanViewResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        MembershipRole viewerRole,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // SESSION-SCOPED entry point (CORE-SVIS-001): the audience decision consults only the rules of
        // THIS session, so a reveal in another concurrent session of the same workspace can never make
        // the resource visible here (the cross-session leak; threat T5/T3). An empty session id can never
        // address a real session, so fail fast (the workspace-wide overload below is the one with no
        // session).
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        return CanViewResourceCoreAsync(
            organizationId, workspaceId, sessionId, viewerRole, resourceType, resourceId, cancellationToken);
    }

    /// <summary>
    /// The single implementation of the role-level audience visibility decision (CORE-VIS-002), always
    /// SESSION-SCOPED (CORE-SVIS-004) — the rule logic lives in exactly ONE place
    /// (docs/05_MODULE_CONTRACTS.md), and it consults only the rules of the supplied session.
    /// </summary>
    private async Task<ResourceVisibilityDecision> CanViewResourceCoreAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        MembershipRole viewerRole,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's resource, so the decision fails fast
        // instead of evaluating an arbitrary rule set (mirrors the repository's empty-id guards).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource id must not be empty.", nameof(resourceId));
        }

        // HOST-CONTENT roles see everything (hidden or visible) — short-circuit to ALLOW before any
        // database read. A host caller never triggers a rule lookup (docs/06 "View host-only content"
        // = yes for Owner/Admin/Host/CoHost).
        if (VisibilityRoles.ViewsHostOnlyContent(viewerRole))
        {
            return ResourceVisibilityDecision.Allow(VisibilityAccessReason.GrantedByHostRole);
        }

        // Roles that are neither host-content nor audience (the audit role, any undefined value) are
        // DENIED by default WITHOUT reading any rule, so no rule existence can leak to a role with no
        // content-view standing (threats T1/T5; docs/06 Auditor = audit-only on both content rows).
        if (!VisibilityRoles.IsAudienceRole(viewerRole))
        {
            return ResourceVisibilityDecision.Deny(VisibilityAccessReason.DeniedRoleNotPermitted);
        }

        // AUDIENCE role (role-level, no specific participant): ALLOW iff an AUDIENCE-WIDE visibility
        // rule makes the resource visible IN THIS SESSION. The lookup is tenant-, workspace- AND
        // session-scoped (organization boundary before workspace boundary before session; resource-level
        // visibility checked last — docs/06 authorization principles), so a rule in another tenant,
        // workspace or session can never make this resource visible (threat T5/T3). A rule scoped to a
        // SPECIFIC participant (CORE-VIS-005) does NOT grant visibility at this role level — only the
        // audience-wide rules do — because a role-level check does not identify a participant; whether a
        // specific participant may see a selected-participant reveal is
        // <see cref="CanParticipantViewResourceAsync(Guid, Guid, Guid, Guid, VisibilityResourceType, Guid, CancellationToken)"/>.
        var rules = await _rules
            .ListByResourceAsync(organizationId, workspaceId, sessionId, resourceType, resourceId, cancellationToken)
            .ConfigureAwait(false);

        return rules.Any(rule => rule.IsVisibleToAudience() && rule.IsAudienceWide)
            ? ResourceVisibilityDecision.Allow(VisibilityAccessReason.GrantedByVisibleRule)
            : ResourceVisibilityDecision.Deny(VisibilityAccessReason.DeniedNotVisible);
    }

    /// <summary>
    /// Decides whether a SPECIFIC participant may see the given resource (CORE-VIS-005) — the
    /// participant-level visibility behind the participant-visible feed. A participant sees a resource
    /// iff some visibility rule makes it visible to THEM: either an AUDIENCE-WIDE visible rule (visible
    /// to everyone) or a visible rule scoped to exactly this participant (a selected-participant
    /// reveal). A visible rule scoped to a DIFFERENT participant does NOT grant access — the
    /// selected-participant guarantee: a non-selected participant must not see a private reveal (threat
    /// T5; docs/06 "Send private content"; docs/09 "selected recipients"). The lookup is tenant-,
    /// workspace- AND session-scoped (CORE-SVIS-001), so a rule in another tenant, workspace or session
    /// never contributes (the organization boundary is checked before the workspace boundary before the
    /// session). This is a per-participant content check, NOT a role check; the caller establishes that
    /// the participant is entitled to a feed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, participant id or resource id is empty.
    /// </exception>
    public Task<bool> CanParticipantViewResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // SESSION-SCOPED entry point (CORE-SVIS-001): visible to this participant iff a rule of THIS
        // session makes it so, so a reveal in a concurrent session of the same workspace never reaches
        // them (the cross-session leak; threat T5/T3). An empty session id can never address a real
        // session, so fail fast.
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        return CanParticipantViewResourceCoreAsync(
            organizationId, workspaceId, sessionId, participantId, resourceType, resourceId, cancellationToken);
    }

    /// <summary>
    /// The single implementation of the per-participant visibility decision (CORE-VIS-005), always
    /// SESSION-SCOPED (CORE-SVIS-004) — the rule logic lives in exactly ONE place
    /// (docs/05_MODULE_CONTRACTS.md), and it consults only the rules of the supplied session.
    /// </summary>
    private async Task<bool> CanParticipantViewResourceCoreAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (participantId == Guid.Empty)
        {
            throw new ArgumentException("Participant id must not be empty.", nameof(participantId));
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource id must not be empty.", nameof(resourceId));
        }

        var rules = await _rules
            .ListByResourceAsync(organizationId, workspaceId, sessionId, resourceType, resourceId, cancellationToken)
            .ConfigureAwait(false);

        // Visible to THIS participant iff some rule is visible AND (audience-wide OR scoped to them).
        return rules.Any(rule => rule.IsVisibleTo(participantId));
    }
}
