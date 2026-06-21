// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The Visibility module's preview-as-participant query (CORE-VIS-003, made participant-aware by
/// CORE-API-004), providing the <c>GetVisibleResourcesForParticipant</c> /
/// <c>PreviewVisibilityForHost</c> operations docs/05_MODULE_CONTRACTS.md assigns to the Visibility
/// module ("audience calculations", "preview-as-participant", "visible state reconstruction"). It
/// computes the SET of resources a SPECIFIC participant may currently see in a SESSION (a reveal is
/// session-scoped, CORE-SVIS-001, so the set is bounded by the session and never includes a reveal made
/// in a concurrent session of the same workspace — the cross-session leak; threat T5/T3) —
/// "Participant visibility is computed server-side" (docs/06_AUTHORIZATION_MATRIX.md). A host invokes
/// it to PREVIEW what a participant sees (preview-as-participant); a participant's own visible feed is
/// the same set. It is a plain, unit-testable query service over explicit inputs, exactly like
/// <see cref="VisibilityPolicy"/> (CORE-VIS-002) and <c>EntitySearchService</c> (CORE-ENT-005);
/// csv/api_routes.csv defines no dedicated preview route, so this service adds NO HTTP endpoint. It is
/// the single source the participant visible-feed projection (CORE-API-005, the
/// GET /api/v1/participants/{participantId}/visible-feed handler) and the entity-search audience
/// filtering (CORE-API-006) consume, so those retrofits never grow a parallel visibility filter.
///
/// REUSES THE CENTRAL DECISION — does NOT re-derive it. The participant-visible set is computed from a
/// SINGLE workspace-rule load (CORE-PERF-004): the tenant- and workspace-scoped
/// <see cref="IVisibilityRuleRepository.ListByWorkspaceAsync"/> is read ONCE, and the visible set is then
/// computed IN MEMORY over those rows by the central policy's in-memory gate
/// <see cref="VisibilityPolicy.ComputeVisibleResourcesForParticipant"/>, which applies the SAME aggregate
/// predicates the per-resource decision
/// <see cref="VisibilityPolicy.CanParticipantViewResourceAsync(System.Guid, System.Guid, System.Guid, System.Guid, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>
/// uses (the CORE-VIS-005 participant-aware primitive made session-scoped by CORE-SVIS-001, the SAME one
/// <see cref="EventRecipientVisibility"/> uses for realtime delivery). So the query count is independent of
/// resource volume — never one policy query per candidate that re-fetches rows the feed already holds (the
/// old 1+M fan-out) — while the REST preview, the realtime recipient calculation and per-resource access
/// still can NEVER diverge and the visibility decision lives in exactly ONE place
/// (docs/05_MODULE_CONTRACTS.md: "Do not duplicate visibility logic elsewhere"; docs/02_ARCHITECTURE.md:
/// "entity visibility is not computed ad hoc in many places"). The visible resources are exactly those that
/// carry at least one rule of THIS session making them visible to this participant (a resource with no such
/// rule is host-only by default and excluded), drawn from the workspace's rules — so a rule in another
/// tenant or workspace can never contribute to this workspace's visible set (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; the organization boundary is checked before the workspace
/// boundary).
///
/// PARTICIPANT-AWARE: AUDIENCE-WIDE AND PRIVATE REVEALS BOTH HANDLED. The result is the set this ONE
/// participant may see: a resource made visible by an AUDIENCE-WIDE rule (visible to everyone) OR by a
/// visible rule scoped to exactly this participant (a selected-participant private reveal). A resource
/// revealed only to a DIFFERENT participant is a candidate (it carries a rule) but is excluded for this
/// participant — the selected-participant guarantee, decided fail-closed by the policy: a non-selected
/// participant must not see a private reveal (threat T5; docs/06 "Send private content"). Two
/// participants therefore see different sets when private reveals are in play.
///
/// AUTHORIZATION IS THE CALLER'S CONCERN. This query computes WHAT a participant sees; WHO may invoke
/// it (the participant who owns the feed, or a Host/CoHost previewing it — the participant-visible-feed
/// authorization model of docs/06) is enforced by the calling endpoint (the CORE-SES-005 visible-feed
/// route, wired in CORE-API-005), exactly as <see cref="VisibilityPolicy"/> trusts the caller to pass a
/// verified participant. This service performs no role authorization itself; it always computes the
/// given participant's content-visibility set.
///
/// SCOPE: this service implements only the preview query. The reveal COMMAND with idempotency and an
/// append-only event is CORE-VIS-004; selected-participant reveal is CORE-VIS-005; audit records are
/// CORE-VIS-006. Wiring this set into the CORE-SES-005 visible-feed response (CORE-API-005) and the
/// CORE-ENT-005 entity-search audience path (CORE-API-006), together with the participant-safe content
/// projection of each resource, are follow-up stories and are deliberately not done here.
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
    /// Computes the set of resources the given participant may currently see in the given session —
    /// the preview-as-participant result. The workspace's rules are loaded ONCE (CORE-PERF-004) and the
    /// visible set is computed IN MEMORY by the central policy's in-memory gate
    /// <see cref="VisibilityPolicy.ComputeVisibleResourcesForParticipant"/>, which applies the SAME decision
    /// the session-scoped
    /// <see cref="VisibilityPolicy.CanParticipantViewResourceAsync(System.Guid, System.Guid, System.Guid, System.Guid, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>
    /// makes — so the participant sees a resource iff an audience-wide visible rule, or a visible rule scoped
    /// to exactly them, applies IN THIS SESSION; only those are returned, in a deterministic order (by
    /// resource type then id). The query count is independent of resource volume (one load, no per-candidate
    /// query). The set is tenant-, workspace- and session-scoped and fails closed (a resource the participant
    /// may not see, including one revealed only to another participant, is excluded).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The participant's workspace whose visible set is computed.</param>
    /// <param name="sessionId">
    /// The session the feed is scoped to (CORE-SVIS-001). A reveal is session-scoped, so the visible set
    /// is the resources revealed IN THIS SESSION that the participant may see; a reveal in a concurrent
    /// session of the same workspace is never in this feed (the cross-session leak; threat T5/T3).
    /// </param>
    /// <param name="participantId">The participant whose visible set is computed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The distinct resources visible to that participant in that session, in deterministic order.</returns>
    /// <exception cref="ArgumentException">The organization id, workspace id, session id or participant id is empty.</exception>
    public async Task<IReadOnlyList<VisibleResource>> GetVisibleResourcesForParticipantAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        // The bare-identity set is the reveal-enriched set projected down to (kind, id), so the single rule
        // load and the in-memory visibility gate live in exactly ONE place (the reveal query below).
        // Identity-only consumers (the audience entity-search filter) keep this shape unchanged.
        var reveals = await GetVisibleResourceRevealsForParticipantAsync(
                organizationId, workspaceId, sessionId, participantId, cancellationToken)
            .ConfigureAwait(false);

        var result = new VisibleResource[reveals.Count];
        for (var index = 0; index < reveals.Count; index++)
        {
            result[index] = new VisibleResource(reveals[index].ResourceType, reveals[index].ResourceId);
        }

        return result;
    }

    /// <summary>
    /// Computes the resources the given participant may currently see in the given session, each ENRICHED
    /// with its audience-safe REVEAL metadata (CORE-APROJ-002) — the participant-feed companion of
    /// <see cref="GetVisibleResourcesForParticipantAsync"/>. The workspace's rules are loaded ONCE
    /// (CORE-PERF-004) and the enriched visible set is computed IN MEMORY by the central policy's
    /// <see cref="VisibilityPolicy.ComputeVisibleResourceRevealsForParticipant"/>, which applies the SAME
    /// session-scoped, fail-closed visibility decision the bare-identity set uses and derives each resource's
    /// reveal scope and reveal time from the SAME granting rules — so the feed's reveal metadata can never
    /// diverge from the visibility decision (docs/05_MODULE_CONTRACTS.md). The audience-safe label/body of each
    /// resource is NOT resolved here (the central security module may not reference the resource modules,
    /// CORE-ARCH-001); the calling endpoint resolves it through the
    /// <see cref="IVisibleResourceAudienceProjector"/> port and combines it with each reveal. The set is
    /// tenant-, workspace- and session-scoped and fails closed exactly as the bare-identity set.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The participant's workspace whose visible set is computed.</param>
    /// <param name="sessionId">The session the feed is scoped to (CORE-SVIS-001).</param>
    /// <param name="participantId">The participant whose visible set is computed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The distinct visible resources, each with its audience-safe reveal metadata, in deterministic order.</returns>
    /// <exception cref="ArgumentException">The organization id, workspace id, session id or participant id is empty.</exception>
    public async Task<IReadOnlyList<VisibleResourceReveal>> GetVisibleResourceRevealsForParticipantAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's rules, a real session or a real participant, so
        // the query fails fast instead of computing over an arbitrary set (mirrors the repository/policy
        // empty-id guards; fail-closed).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (participantId == Guid.Empty)
        {
            throw new ArgumentException("Participant id must not be empty.", nameof(participantId));
        }

        // THE single rule load (CORE-PERF-004): read the workspace's rules ONCE, then compute the visible set
        // IN MEMORY over the rows already held — never one per-candidate policy query that re-fetches rows the
        // feed already has (the old 1+M fan-out). The list is tenant- and workspace-scoped (organization
        // boundary before workspace boundary), so no foreign tenant's or workspace's rule contributes
        // (threat T5); it spans the workspace's sessions and is filtered to THIS session in memory below.
        var workspaceRules = await _rules
            .ListByWorkspaceAsync(organizationId, workspaceId, cancellationToken)
            .ConfigureAwait(false);

        // Compute the participant's enriched visible set in memory through the central policy's in-memory gate
        // (CORE-PERF-004): a resource is visible iff some SESSION-SCOPED rule makes it visible to THIS
        // participant — the SAME decision CanParticipantViewResourceAsync makes (rules.Any(IsVisibleTo)),
        // applied over the pre-loaded rules — and each visible resource carries the audience-safe reveal scope
        // and time derived from the SAME granting rules. So this REST feed, per-resource access and the
        // realtime recipient gate can never diverge and the visibility decision lives in exactly one place
        // (docs/05). A reveal to a different participant, or a reveal in a different session, is excluded
        // (fail-closed; the selected-participant and session-scope guarantees), and the result is distinct and
        // deterministically ordered.
        return _policy.ComputeVisibleResourceRevealsForParticipant(sessionId, participantId, workspaceRules);
    }
}
