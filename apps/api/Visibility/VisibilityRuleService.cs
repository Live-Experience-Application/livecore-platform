// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Visibility;

/// <summary>
/// The visibility-rule CREATE command of the Visibility module (CORE-SVIS-005, the "Vertical Adopter
/// Consumability Completeness" epic). An authoring role can author a new <see cref="VisibilityRule"/> for a
/// resource in a session over HTTP, so a vertical can configure session-scoped visibility through the API
/// rather than only through the live reveal/hide commands. This service is the authorized command's EFFECT:
/// the endpoint performs authentication, tenant resolution and the authoring-role authorization before
/// calling in, exactly as the entity create command splits its endpoint from
/// <see cref="LiveCore.Api.Entities.EntityCreationService"/>.
///
/// SAME-WORKSPACE INVARIANT enforced SERVER-SIDE (the story's headline requirement). A
/// <see cref="VisibilityRule"/>'s <c>resource_id</c> is a POLYMORPHIC reference across
/// scenes/content_blocks/entities (no database foreign key can enforce it; see
/// <see cref="VisibilityRule"/>), so this service resolves the referenced resource through the
/// <see cref="IVisibilityResourceWorkspaceLocator"/> port WITHIN the rule's own (organization, workspace)
/// before creating anything: a resource in another workspace or tenant, or an unknown resource, yields
/// <see cref="VisibilityRuleCreationResult.ResourceNotInWorkspace"/> — no rule is created and no transaction
/// is opened. A SELECTED-participant rule (CORE-VIS-005) additionally resolves the target participant within
/// the same workspace (the same coupling the reveal command enforces); a cross-workspace/unknown participant
/// yields <see cref="VisibilityRuleCreationResult.ParticipantNotInWorkspace"/>. The surrogate id alone never
/// authorizes anything; every lookup is scoped by (organization, workspace), so neither the resource nor the
/// participant reference can reach across a workspace or tenant boundary (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// REUSES THE VISIBILITY DOMAIN — no parallel rule logic. The rule is built through the CORE-VIS-001
/// aggregate factories (<see cref="VisibilityRule.Create"/> for an audience-wide rule,
/// <see cref="VisibilityRule.CreateForParticipant"/> for a selected-participant rule) and persisted through
/// the module's <see cref="IVisibilityRuleRepository"/>. The single-rule-per-(session, resource, DIMENSION)
/// uniqueness (CORE-SVIS-002) is honored: a second rule in the same dimension is rejected by the filtered
/// unique index and surfaces as <see cref="VisibilityRuleCreationResult.Duplicate"/> rather than creating a
/// ghost duplicate.
///
/// AUDIT (the story's "created rules are audited" criterion). A creation is security-relevant, so the create
/// appends an append-only <see cref="AuditAction.VisibilityRuleChanged"/> record through the SAME
/// <see cref="AuditLogEntry.ForVisibilityRuleChange"/> producer the reveal/hide command uses (CORE-VIS-006):
/// it records the authoring actor, the governed resource, the optional selected-participant target and the
/// before/after state — the rule had NO prior state (a fresh create), so the previous state is null and the
/// new state is the created visibility. The audit row carries only identifiers, an enum and a generic state
/// name — never resolved content (threat T7).
///
/// ATOMICITY. The rule insert and the audit append run inside ONE <see cref="TransactionalUnitOfWork"/>
/// (CORE-CONC-002), so either both commit or a failure rolls both back — a creation is recorded as a fact or
/// it does not happen. The transaction is opened inside the EF execution strategy, so it stays correct under
/// the CORE-CONC-003 retrying strategy. The same-workspace resolution runs BEFORE the transaction (mirroring
/// how <see cref="LiveCore.Api.Entities.EntityCreationService"/> resolves the type first).
///
/// AUTHORING, NOT A LIVE REVEAL. This is the configuration surface for rules; it deliberately does NOT emit
/// the durable realtime session events (ContentRevealed/VisibilityRuleChanged) the reveal/hide COMMANDS emit.
/// Those commands remain the live "show/hide it now" path. Creating a rule still takes effect for every
/// subsequent visibility decision (the central <see cref="VisibilityPolicy"/> reads the rules), so there is
/// no security gap — only no proactive realtime push.
/// </summary>
internal sealed class VisibilityRuleService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IVisibilityRuleRepository _rules;
    private readonly IVisibilityResourceWorkspaceLocator _resourceLocator;
    private readonly IParticipantRepository _participants;
    private readonly IAuditLogRepository _audit;

    public VisibilityRuleService(
        TransactionalUnitOfWork unitOfWork,
        IVisibilityRuleRepository rules,
        IVisibilityResourceWorkspaceLocator resourceLocator,
        IParticipantRepository participants,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(resourceLocator);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _rules = rules;
        _resourceLocator = resourceLocator;
        _participants = participants;
        _audit = audit;
    }

    /// <summary>
    /// Creates a new visibility rule for the given resource in the given session (owned by the given
    /// workspace and tenant) with the given base visibility, enforcing the same-workspace invariant and
    /// appending an append-only audit record — atomically. Returns
    /// <see cref="VisibilityRuleCreationResult.Created"/> on success, or one of the gated outcomes
    /// (resource/participant not in the workspace, or a duplicate dimension) when the rule cannot be created.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The session's workspace the rule (and its resource) belong to.</param>
    /// <param name="sessionId">The session the rule is scoped to (a reveal is session-scoped, CORE-SVIS-001).</param>
    /// <param name="resourceType">The kind of resource the rule governs (Scene/ContentBlock/Entity).</param>
    /// <param name="resourceId">The surrogate id of the resource the rule governs (resolved within the workspace).</param>
    /// <param name="visibility">The base audience visibility the rule assigns (Hidden or Visible).</param>
    /// <param name="targetParticipantId">
    /// The participant a SELECTED-participant rule applies to (resolved within the workspace), or
    /// <see langword="null"/> for an AUDIENCE-WIDE rule.
    /// </param>
    /// <param name="actorUserProfileId">
    /// The authenticated authoring role who created the rule — the audited actor. Supplied by the endpoint
    /// from the resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the rule's created/updated time and the audit record's time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, session id, resource id or actor id is empty, or the target
    /// participant id is explicitly empty (pass null for an audience-wide rule).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The resource type or the visibility state is not defined.</exception>
    public async Task<VisibilityRuleCreationResult> CreateAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        VisibilityState visibility,
        Guid? targetParticipantId,
        Guid actorUserProfileId,
        DateTimeOffset now,
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

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (!VisibilityRule.IsValidResourceType(resourceType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resourceType),
                resourceType,
                "Resource type is not a defined visibility resource type.");
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource id must not be empty.", nameof(resourceId));
        }

        if (!VisibilityRule.IsValidVisibility(visibility))
        {
            throw new ArgumentOutOfRangeException(
                nameof(visibility),
                visibility,
                "Visibility is not a defined visibility state.");
        }

        if (targetParticipantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target participant id must not be empty; pass null for an audience-wide rule.",
                nameof(targetParticipantId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // SAME-WORKSPACE INVARIANT: the referenced resource must exist IN THE RULE'S OWN workspace. The
        // locator resolves it through the owning resource module's tenant- and workspace-scoped lookup, so a
        // resource in another workspace/tenant (or an unknown id) is never borrowed; an unresolved resource
        // creates nothing and opens no transaction — rejected as ResourceNotInWorkspace (threats T1/T5).
        var resourceExists = await _resourceLocator
            .ResourceExistsInWorkspaceAsync(organizationId, workspaceId, resourceType, resourceId, cancellationToken)
            .ConfigureAwait(false);
        if (!resourceExists)
        {
            return VisibilityRuleCreationResult.ResourceNotInWorkspace;
        }

        // A SELECTED-participant rule's target must likewise be a participant of the rule's OWN workspace (the
        // same coupling the reveal command enforces): resolve it within the resolved tenant and verify its
        // workspace, so a cross-workspace/unknown participant is rejected as ParticipantNotInWorkspace and
        // never created — a host must not be able to target, or probe for, a participant outside the session's
        // workspace (threat T5).
        if (targetParticipantId is { } participantId)
        {
            var participant = await _participants
                .FindByIdInOrganizationAsync(organizationId, participantId, cancellationToken)
                .ConfigureAwait(false);
            if (participant is null || participant.WorkspaceId != workspaceId)
            {
                return VisibilityRuleCreationResult.ParticipantNotInWorkspace;
            }
        }

        // ONE unit of work (CORE-CONC-002): the rule insert and the audit append commit together or roll back
        // together, so a creation is applied whole or not at all. Both repositories write through the same
        // scoped DbContext the TransactionalUnitOfWork begins the transaction on. The transaction is opened
        // inside the EF execution strategy's ExecuteAsync, so it stays correct under the CORE-CONC-003
        // retrying strategy.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // The aggregate mints the server-side surrogate id (UUIDv7) and fixes the tenant, workspace,
                // session and governed resource immutably (the same factories the reveal command uses).
                var rule = targetParticipantId is { } selectedParticipantId
                    ? VisibilityRule.CreateForParticipant(
                        organizationId,
                        workspaceId,
                        sessionId,
                        resourceType,
                        resourceId,
                        selectedParticipantId,
                        visibility,
                        now)
                    : VisibilityRule.Create(
                        organizationId,
                        workspaceId,
                        sessionId,
                        resourceType,
                        resourceId,
                        visibility,
                        now);

                // SINGLE-RULE-PER-DIMENSION (CORE-SVIS-002): a second rule in the same (session, resource,
                // dimension) is rejected by the filtered unique index and surfaces as Duplicate; nothing is
                // audited (no rule was created).
                var addResult = await _rules.AddAsync(rule, transactionCancellationToken).ConfigureAwait(false);
                if (addResult == VisibilityRuleAddResult.Duplicate)
                {
                    return VisibilityRuleCreationResult.Duplicate;
                }

                // AUDIT (the story's "created rules are audited" criterion): a fresh create has NO prior state,
                // so the previous state is null and the new state is the created visibility. The record carries
                // only identifiers, an enum and the generic state name — never resolved content (threat T7).
                var entry = AuditLogEntry.ForVisibilityRuleChange(
                    organizationId,
                    workspaceId,
                    actorUserProfileId,
                    resourceType.ToString(),
                    resourceId,
                    targetParticipantId,
                    previousState: null,
                    visibility.ToString(),
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                return VisibilityRuleCreationResult.Created(rule);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
