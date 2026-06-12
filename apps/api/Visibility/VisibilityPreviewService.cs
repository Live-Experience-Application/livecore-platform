using LiveCore.Api.Organizations;

namespace LiveCore.Api.Visibility;

/// <summary>
/// The Visibility module's preview-as-participant query (CORE-VIS-003), providing the
/// <c>GetVisibleResourcesForParticipant</c> / <c>PreviewVisibilityForHost</c> operations
/// docs/05_MODULE_CONTRACTS.md assigns to the Visibility module ("audience calculations",
/// "preview-as-participant", "visible state reconstruction"). It computes the SET of resources an
/// audience participant may currently see in a workspace — "Participant visibility is computed
/// server-side" (docs/06_AUTHORIZATION_MATRIX.md). A host invokes it to PREVIEW what a participant
/// sees (preview-as-participant); a participant's own visible feed is the same set. It is a plain,
/// unit-testable query service over explicit inputs, exactly like
/// <see cref="VisibilityPolicy"/> (CORE-VIS-002) and <c>EntitySearchService</c> (CORE-ENT-005);
/// csv/api_routes.csv defines no dedicated preview route, so this story adds NO HTTP endpoint (the
/// participant-visible-feed route GET /api/v1/participants/{participantId}/visible-feed is the
/// CORE-SES-005 skeleton; wiring this query into it, alongside the Realtime reveal-event projection,
/// is a later step — see the scope note below).
///
/// REUSES THE CENTRAL DECISION — does NOT re-derive it. The audience-visible set is computed by
/// routing every candidate resource through <see cref="VisibilityPolicy.CanViewResourceAsync"/> (the
/// CORE-VIS-002 authority) under the audience viewpoint (<see cref="MembershipRole.Participant"/>), so
/// preview-as-participant and per-resource access can NEVER diverge and the visibility decision lives
/// in exactly ONE place (docs/05_MODULE_CONTRACTS.md: "Do not duplicate visibility logic elsewhere";
/// docs/02_ARCHITECTURE.md: "entity visibility is not computed ad hoc in many places"). The candidate
/// resources are exactly the resources that have at least one visibility rule in the workspace
/// (a resource with no rule is host-only by default and the policy denies it to the audience), drawn
/// from the tenant- and workspace-scoped <see cref="IVisibilityRuleRepository.ListByWorkspaceAsync"/>
/// — so a rule in another tenant or workspace can never contribute to this workspace's visible set
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; the organization boundary is checked before the
/// workspace boundary).
///
/// AUDIENCE-WIDE FOR NOW. Because the CORE-VIS-001 rules are audience-wide (a resource is visible to
/// the whole audience or to none), the visible set does not yet depend on WHICH participant is
/// previewed — it is the workspace's audience-visible set. Restricting visibility to a SELECTED
/// subset of participants (so two participants see different sets) is the later selected-participant
/// reveal (CORE-VIS-005); when it lands, this query gains the per-participant refinement. The
/// participant's workspace is the input; the participant's identity does not refine the result yet,
/// so it is deliberately not a parameter (adding one now would be dead input).
///
/// AUTHORIZATION IS THE CALLER'S CONCERN. This query computes WHAT an audience participant sees; WHO
/// may invoke it (the participant who owns the feed, or a Host/CoHost previewing it — the
/// participant-visible-feed authorization model of docs/06) is enforced by the calling endpoint (the
/// CORE-SES-005 visible-feed route), exactly as <see cref="VisibilityPolicy"/> trusts the caller to
/// pass a verified role. This service performs no role authorization itself; it always returns the
/// audience viewpoint.
///
/// SCOPE: this story implements only the preview query. The reveal COMMAND with idempotency and an
/// append-only event is CORE-VIS-004; selected-participant reveal is CORE-VIS-005; audit records are
/// CORE-VIS-006. Wiring this set into the CORE-SES-005 visible-feed response (and the CORE-ENT-005
/// entity-search audience path), together with the participant-safe content projection of each
/// resource, is a follow-up that also depends on the Realtime reveal-event model — it is deliberately
/// not done here.
/// </summary>
internal sealed class VisibilityPreviewService
{
    private readonly IVisibilityRuleRepository _rules;
    private readonly VisibilityPolicy _policy;

    public VisibilityPreviewService(IVisibilityRuleRepository rules, VisibilityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(policy);
        _rules = rules;
        _policy = policy;
    }

    /// <summary>
    /// Computes the set of resources an audience participant may currently see in the given workspace
    /// — the preview-as-participant result. Every candidate resource (any resource with a visibility
    /// rule in the workspace) is decided by <see cref="VisibilityPolicy.CanViewResourceAsync"/> under
    /// the audience viewpoint, and only those it allows are returned, in a deterministic order
    /// (by resource type then id). The set is tenant- and workspace-scoped.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The participant's workspace whose visible set is computed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The distinct resources visible to the audience, in deterministic order.</returns>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    public async Task<IReadOnlyList<VisibleResource>> GetVisibleResourcesForParticipantAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's rules, so the query fails fast instead of
        // computing over an arbitrary set (mirrors the repository/policy empty-id guards).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // Candidate resources = exactly the resources that carry at least one rule in this workspace
        // (a rule-less resource is host-only by default and the policy denies it to the audience). The
        // list is tenant- and workspace-scoped, so no foreign tenant/workspace rule contributes
        // (threat T5). Distinct, deterministically ordered, so the result is stable.
        var allRules = await _rules
            .ListByWorkspaceAsync(organizationId, workspaceId, cancellationToken)
            .ConfigureAwait(false);

        var candidates = allRules
            .Select(rule => new VisibleResource(rule.ResourceType, rule.ResourceId))
            .Distinct()
            .OrderBy(resource => resource.ResourceType)
            .ThenBy(resource => resource.ResourceId)
            .ToArray();

        var visible = new List<VisibleResource>(candidates.Length);
        foreach (var candidate in candidates)
        {
            // Route every candidate through the canonical CanViewResource decision under the audience
            // viewpoint, so preview-as-participant can never diverge from per-resource access and the
            // visibility decision lives in exactly one place (docs/05).
            var decision = await _policy
                .CanViewResourceAsync(
                    organizationId,
                    workspaceId,
                    MembershipRole.Participant,
                    candidate.ResourceType,
                    candidate.ResourceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (decision.CanView)
            {
                visible.Add(candidate);
            }
        }

        return visible;
    }
}
