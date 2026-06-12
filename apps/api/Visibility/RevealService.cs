using LiveCore.Api.Audit;
using LiveCore.Api.SystemModule;

namespace LiveCore.Api.Visibility;

/// <summary>
/// The reveal command of the Visibility module (CORE-VIS-004) — the "Command/action that changes
/// visibility" (docs/03_DOMAIN_LANGUAGE.md: Reveal) that makes a resource VISIBLE to the audience,
/// IDEMPOTENTLY for client retry (docs/08_API_CONTRACTS.md). It is a plain decision/command service
/// over <see cref="IVisibilityRuleRepository"/> and <see cref="IIdempotencyKeyStore"/> taking explicit
/// inputs, exactly like <see cref="VisibilityPolicy"/> (CORE-VIS-002); the calling endpoint resolves
/// the tenant, the session's workspace and authorizes the caller's role before invoking it.
///
/// IDEMPOTENCY (the headline). The reveal is made exactly-once for a given client
/// <c>Idempotency-Key</c> by combining two mechanisms:
/// <list type="bullet">
///   <item>RETRY SHORT-CIRCUIT: before doing anything, the recorded idempotency keys are checked
///   (<see cref="IIdempotencyKeyStore.FindAsync"/>); a hit means this exact request was already
///   processed, so the reveal is NOT re-applied (<see cref="RevealOutcome.AlreadyApplied"/>). This is
///   what stops a duplicate effect (and, once the Realtime epic adds the reveal EVENT, a duplicate
///   event) on a client retry.</item>
///   <item>STATE-LEVEL IDEMPOTENCE: the underlying effect, <see cref="EnsureVisibleAsync"/>, only
///   ever ENSURES the resource is visible — if a visibility rule already makes it visible it does
///   nothing — so even a concurrent double-execution cannot move the resource past "visible". The
///   unique <c>idempotency_keys(scope, key)</c> index then makes the key record itself single: a
///   concurrent record loses with <see cref="IdempotencyKeyAddResult.Duplicate"/> and is reported as
///   an idempotent retry. No distributed transaction is needed because the effect is idempotent.</item>
/// </list>
/// The idempotency scope is per-tenant (<c>reveal:{organizationId}</c>), so one tenant's keys never
/// collide with another's (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// REUSES THE VISIBILITY RULE — does NOT re-derive visibility. The reveal works through the
/// CORE-VIS-001 <see cref="VisibilityRule"/> aggregate: it flips an existing rule to
/// <see cref="VisibilityState.Visible"/> via the aggregate's <see cref="VisibilityRule.ChangeVisibility"/>
/// primitive, or creates a visible rule when the resource has none. The rule reads/writes are tenant-
/// and workspace-scoped (organization boundary before workspace boundary; threat T5). The reveal is
/// either audience-wide or scoped to one selected participant (CORE-VIS-005). The same-workspace
/// coupling of the target resource (that the resource id refers to a real resource in the workspace) is
/// the documented carry-over deferred to a resource-resolution step, mirroring
/// <c>ContentBlock.SceneId</c> / <c>Entity.EntityTypeId</c>; this command sets the rule by (type, id)
/// and does not yet resolve the resource.
///
/// AUDIT (CORE-VIS-006). When — and only when — a reveal ACTUALLY changes a rule's visibility (a new
/// visible rule is created, or a hidden rule is flipped to visible), the command appends an append-only
/// <see cref="AuditLogEntry"/> recording the change: the tenant, the workspace, the authenticated actor,
/// the governed resource, the optional selected-participant target and the before/after visibility state
/// (<see cref="IAuditLogRepository"/>; the Audit module owns the <c>audit_logs</c> table). This is the
/// docs/07_SECURITY_THREAT_MODEL.md required control "audit creation for visibility changes". The audit
/// is written through the idempotency gate, so it inherits the command's exactly-once property: a retry
/// with the same key short-circuits BEFORE the effect and writes no second audit record, and a no-op
/// reveal of an already-visible resource records nothing (there was no change to audit). Distinct from
/// the audit is the durable realtime <c>ContentRevealed</c>/<c>VisibilityRuleChanged</c> SESSION event
/// (docs/09_EVENT_CATALOG.md): its emission and delivery are still deferred to the Realtime epic
/// (CORE-RT-003), exactly as CORE-SES-004 deferred <c>SessionStarted</c>/<c>SessionEnded</c> — there is
/// no event store/transport yet. This story delivers the security AUDIT record, not the realtime event.
/// </summary>
internal sealed class RevealService
{
    private readonly IVisibilityRuleRepository _rules;
    private readonly IIdempotencyKeyStore _idempotency;
    private readonly IAuditLogRepository _audit;

    public RevealService(
        IVisibilityRuleRepository rules,
        IIdempotencyKeyStore idempotency,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(audit);
        _rules = rules;
        _idempotency = idempotency;
        _audit = audit;
    }

    /// <summary>
    /// Reveals the given resource to the audience in the given workspace, idempotently for the given
    /// client idempotency key. Returns <see cref="RevealOutcome.Applied"/> the first time a key is
    /// seen and <see cref="RevealOutcome.AlreadyApplied"/> on a retry; after either the resource is
    /// visible.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The session's workspace the resource belongs to.</param>
    /// <param name="resourceType">The kind of resource to reveal.</param>
    /// <param name="resourceId">The surrogate id of the resource to reveal.</param>
    /// <param name="targetParticipantId">
    /// The participant to reveal to (a SELECTED-participant reveal, CORE-VIS-005), or
    /// <see langword="null"/> to reveal to the WHOLE audience. When set it must be a real participant
    /// id; the caller is responsible for having resolved it within the resource's workspace.
    /// </param>
    /// <param name="actorUserProfileId">
    /// The authenticated user who executed the reveal — the audited actor (CORE-VIS-006,
    /// docs/09_EVENT_CATALOG.md <c>createdBy</c>). The caller (the endpoint) supplies it from the
    /// resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="idempotencyKey">The client <c>Idempotency-Key</c> header value.</param>
    /// <param name="now">The command timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, resource id or actor id is empty, the target participant id is
    /// empty, or the idempotency key is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The resource type is not defined.</exception>
    public async Task<RevealResult> RevealAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        Guid actorUserProfileId,
        string idempotencyKey,
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

        if (targetParticipantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target participant id must not be empty; pass null to reveal to the whole audience.",
                nameof(targetParticipantId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "Actor user profile id must not be empty.",
                nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("Idempotency key must not be empty.", nameof(idempotencyKey));
        }

        var scope = BuildScope(organizationId);

        // RETRY SHORT-CIRCUIT: if this key was already recorded, the request was already processed;
        // do not re-apply (no duplicate effect / future duplicate event).
        var existing = await _idempotency.FindAsync(scope, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return RevealResult.AlreadyApplied(resourceType, resourceId, targetParticipantId);
        }

        // Apply the (state-idempotent) effect, then record the key. Recording after the effect means
        // a crash between the two leaves the key unrecorded, so a retry safely re-ensures visibility
        // (the effect is idempotent) rather than skipping it.
        var change = await EnsureVisibleAsync(
                organizationId, workspaceId, resourceType, resourceId, targetParticipantId, now, cancellationToken)
            .ConfigureAwait(false);

        // AUDIT (CORE-VIS-006): append an append-only audit record IFF the visibility actually changed.
        // This sits inside the retry short-circuit (a repeat key returned above) and after the effect, so
        // a retry writes no second record and a no-op reveal (already visible) records nothing — only a
        // real change is audited (docs/07_SECURITY_THREAT_MODEL.md: "audit creation for visibility
        // changes").
        if (change is { } appliedChange)
        {
            var entry = AuditLogEntry.ForVisibilityRuleChange(
                organizationId,
                workspaceId,
                actorUserProfileId,
                resourceType.ToString(),
                resourceId,
                targetParticipantId,
                appliedChange.PreviousVisibility?.ToString(),
                appliedChange.NewVisibility.ToString(),
                now);
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        var recorded = await _idempotency
            .AddAsync(IdempotencyKey.Create(scope, idempotencyKey, now), cancellationToken)
            .ConfigureAwait(false);

        // A concurrent request recorded the same key first: report this one as an idempotent retry
        // (the effect it applied is the same idempotent "ensure visible"). On a first apply, surface
        // whether the visibility ACTUALLY changed (the same signal the audit used) so the endpoint emits
        // the durable ContentRevealed event exactly once per real change (CORE-RT-003).
        return recorded == IdempotencyKeyAddResult.Duplicate
            ? RevealResult.AlreadyApplied(resourceType, resourceId, targetParticipantId)
            : RevealResult.Applied(resourceType, resourceId, targetParticipantId, visibilityChanged: change is not null);
    }

    /// <summary>
    /// Ensures the given resource is visible IN THE TARGET DIMENSION, idempotently, and reports the
    /// visibility CHANGE it made (or <see langword="null"/> when nothing changed) so the caller can
    /// audit exactly the real changes (CORE-VIS-006). The target dimension is either audience-wide
    /// (<paramref name="targetParticipantId"/> is <see langword="null"/>) or one selected participant;
    /// the two are independent — an audience-wide reveal never touches a participant-scoped rule and
    /// vice versa, so a private reveal to one participant never widens visibility for anyone else.
    /// Within the matching dimension: if a rule already makes the resource visible, does nothing and
    /// returns <see langword="null"/> (no change to audit); otherwise flips an existing (hidden) rule in
    /// that dimension to visible (recording the prior Hidden state), or creates a visible rule when none
    /// exists for it (no prior state). All reads/writes are tenant- and workspace-scoped.
    /// </summary>
    private async Task<VisibilityChange?> EnsureVisibleAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rules = await _rules
            .ListByResourceAsync(organizationId, workspaceId, resourceType, resourceId, cancellationToken)
            .ConfigureAwait(false);

        // Only the rules in the SAME target dimension are relevant: audience-wide rules for an
        // audience-wide reveal, or rules scoped to exactly this participant for a selected reveal.
        var inDimension = rules
            .Where(rule => rule.TargetParticipantId == targetParticipantId)
            .ToArray();

        // Already visible in this dimension: nothing to do (state-level idempotence), nothing to audit.
        if (inDimension.Any(rule => rule.IsVisibleToAudience()))
        {
            return null;
        }

        // A hidden rule exists in this dimension: flip it to visible (the CORE-VIS-001 primitive)
        // rather than accumulating a second rule. Capture the prior state BEFORE the change for the audit.
        var hiddenRule = inDimension.Length > 0 ? inDimension[0] : null;
        if (hiddenRule is not null)
        {
            var previousVisibility = hiddenRule.Visibility;
            hiddenRule.ChangeVisibility(VisibilityState.Visible, now);
            await _rules.UpdateAsync(hiddenRule, cancellationToken).ConfigureAwait(false);
            return new VisibilityChange(previousVisibility, VisibilityState.Visible);
        }

        // No rule in this dimension yet: create a visible one — audience-wide or scoped to the
        // participant, per the target. There is no prior state.
        var created = targetParticipantId is { } participantId
            ? VisibilityRule.CreateForParticipant(
                organizationId, workspaceId, resourceType, resourceId, participantId, VisibilityState.Visible, now)
            : VisibilityRule.Create(
                organizationId, workspaceId, resourceType, resourceId, VisibilityState.Visible, now);
        await _rules.AddAsync(created, cancellationToken).ConfigureAwait(false);
        return new VisibilityChange(PreviousVisibility: null, NewVisibility: VisibilityState.Visible);
    }

    /// <summary>
    /// A visibility change applied by <see cref="EnsureVisibleAsync"/>: the state BEFORE the change
    /// (<see langword="null"/> when no rule existed) and the state AFTER. Used to write the audit record.
    /// </summary>
    private readonly record struct VisibilityChange(
        VisibilityState? PreviousVisibility,
        VisibilityState NewVisibility);

    /// <summary>
    /// Builds the per-tenant idempotency scope for reveal commands, so a client key is idempotent
    /// within a tenant's reveals and never collides across tenants (threat T5).
    /// </summary>
    private static string BuildScope(Guid organizationId) => $"reveal:{organizationId}";
}
