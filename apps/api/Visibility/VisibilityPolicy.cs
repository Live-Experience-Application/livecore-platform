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
/// workspace boundary, and the workspace boundary before resource-level visibility — the rule lookup
/// leads with <c>organization_id</c> then <c>workspace_id</c>, so a rule in another tenant or
/// workspace can never make a resource visible here (threat T5). The caller is responsible for having
/// resolved the workspace <see cref="MembershipRole"/> from a verified membership (the tenant context
/// resolver); this policy trusts the passed role exactly as <c>EntitySearchService</c> does and
/// decides only the content-visibility question.
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
    /// Decides whether the given workspace role may view the given resource, applying the host-content
    /// vs audience-visible rules of docs/06_AUTHORIZATION_MATRIX.md over the resource's visibility
    /// rules.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the resource belongs to.</param>
    /// <param name="viewerRole">The caller's role in <paramref name="workspaceId"/>.</param>
    /// <param name="resourceType">The kind of resource being viewed.</param>
    /// <param name="resourceId">The surrogate id of the resource being viewed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An ALLOW decision (with the granting reason) for a host role, or for an audience role when a
    /// visibility rule makes the resource visible; otherwise a DENY decision (with the denying
    /// reason).
    /// </returns>
    /// <exception cref="ArgumentException">The organization id, workspace id or resource id is empty.</exception>
    public async Task<ResourceVisibilityDecision> CanViewResourceAsync(
        Guid organizationId,
        Guid workspaceId,
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

        // AUDIENCE role: ALLOW iff a visibility rule makes the resource visible to the audience. The
        // lookup is tenant- and workspace-scoped (organization boundary before workspace boundary;
        // resource-level visibility checked last — docs/06 authorization principles), so a rule in
        // another tenant or workspace can never make this resource visible (threat T5). Any visible
        // rule grants visibility (the index is non-unique).
        var rules = await _rules
            .ListByResourceAsync(organizationId, workspaceId, resourceType, resourceId, cancellationToken)
            .ConfigureAwait(false);

        return rules.Any(rule => rule.IsVisibleToAudience())
            ? ResourceVisibilityDecision.Allow(VisibilityAccessReason.GrantedByVisibleRule)
            : ResourceVisibilityDecision.Deny(VisibilityAccessReason.DeniedNotVisible);
    }
}
