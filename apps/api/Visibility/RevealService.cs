using LiveCore.Api.Audit;
using LiveCore.Api.SystemModule;

namespace LiveCore.Api.Visibility;

/// <summary>
/// The reveal AND hide commands of the Visibility module — the two "Command/action that changes
/// visibility" operations (docs/03_DOMAIN_LANGUAGE.md: Reveal) that move a resource's audience
/// visibility, IDEMPOTENTLY for client retry (docs/08_API_CONTRACTS.md). <see cref="RevealAsync"/>
/// (CORE-VIS-004) makes a resource VISIBLE to the audience; <see cref="HideAsync"/> (CORE-REV-001, the
/// "Reveal Lifecycle" hide / un-reveal) takes a reveal back so a previously visible resource becomes
/// <see cref="VisibilityState.Hidden"/> again. Both are the SAME idempotent command with opposite target
/// states, so they share one engine (<see cref="ApplyVisibilityChangeAsync"/>) — hide is not a parallel
/// duplicate of reveal. It is a plain decision/command service over
/// <see cref="IVisibilityRuleRepository"/> and <see cref="IIdempotencyKeyStore"/> taking explicit inputs,
/// exactly like <see cref="VisibilityPolicy"/> (CORE-VIS-002); the calling endpoint resolves the tenant,
/// the session's workspace and authorizes the caller's role before invoking it.
///
/// IDEMPOTENCY (the headline). Each command is made exactly-once for a given client
/// <c>Idempotency-Key</c> by combining two mechanisms:
/// <list type="bullet">
///   <item>RETRY SHORT-CIRCUIT: before doing anything, the recorded idempotency keys are checked
///   (<see cref="IIdempotencyKeyStore.FindAsync"/>); a hit means this exact request was already
///   processed, so the change is NOT re-applied (the <c>AlreadyApplied</c> outcome). This is what stops
///   a duplicate effect and a duplicate realtime event on a client retry.</item>
///   <item>STATE-LEVEL IDEMPOTENCE: the underlying effect, <see cref="EnsureStateAsync"/>, only ever
///   ENSURES the resource is in the TARGET state — reveal ensures Visible, hide ensures Hidden — and is a
///   no-op when it is already there, so even a concurrent double-execution cannot move the resource past
///   the target. The unique <c>idempotency_keys(scope, key)</c> index then makes the key record itself
///   single: a concurrent record loses with <see cref="IdempotencyKeyAddResult.Duplicate"/> and is
///   reported as an idempotent retry. No distributed transaction is needed because the effect is
///   idempotent.</item>
/// </list>
/// The idempotency scope is per-tenant AND per-operation (<c>reveal:{organizationId}</c> vs
/// <c>hide:{organizationId}</c>), so one tenant's keys never collide with another's (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md) and a reveal key is never confused with a hide key (a client may use
/// the same key value for the matching reveal/hide pair without one short-circuiting the other).
///
/// REUSES THE VISIBILITY RULE — does NOT re-derive visibility. Both commands work through the
/// CORE-VIS-001 <see cref="VisibilityRule"/> aggregate and its
/// <see cref="VisibilityRule.ChangeVisibility"/> primitive: reveal flips an existing hidden rule to
/// <see cref="VisibilityState.Visible"/> (or creates a visible rule when none exists); hide flips an
/// existing visible rule to <see cref="VisibilityState.Hidden"/> (and, because an absent rule already
/// means hidden, creates NOTHING when no rule exists — the un-reveal of an already-hidden resource is a
/// no-op). The rule reads/writes are tenant- and workspace-scoped (organization boundary before workspace
/// boundary; threat T5). Each command acts in ONE dimension — audience-wide or scoped to one selected
/// participant (CORE-VIS-005) — and the two dimensions are independent, so hiding a participant's private
/// reveal never touches the audience-wide rule and vice versa. The same-workspace coupling of the target
/// resource (that the resource id refers to a real resource in the workspace) is the documented carry-over
/// deferred to a resource-resolution step, mirroring <c>ContentBlock.SceneId</c> /
/// <c>Entity.EntityTypeId</c>; the command sets the rule by (type, id) and does not yet resolve the
/// resource.
///
/// AUDIT (CORE-VIS-006). When — and only when — a command ACTUALLY changes a rule's visibility (a new
/// visible rule is created, a hidden rule is flipped to visible, or a visible rule is flipped to hidden),
/// it appends an append-only <see cref="AuditLogEntry"/> recording the change: the tenant, the workspace,
/// the authenticated actor, the governed resource, the optional selected-participant target and the
/// before/after visibility state (<see cref="IAuditLogRepository"/>; the Audit module owns the
/// <c>audit_logs</c> table). This is the docs/07_SECURITY_THREAT_MODEL.md required control "audit creation
/// for visibility changes". The audit is written through the idempotency gate, so it inherits the
/// command's exactly-once property: a retry with the same key short-circuits BEFORE the effect and writes
/// no second audit record, and a no-op (revealing an already-visible, or hiding an already-hidden,
/// resource) records nothing (there was no change to audit). The matching durable realtime SESSION event
/// (<c>ContentRevealed</c> / <c>ContentHidden</c>, docs/09_EVENT_CATALOG.md) is emitted by the endpoint on
/// the SAME change signal and is distinct from this audit record.
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
        var outcome = await ApplyVisibilityChangeAsync(
                _revealScopePrefix,
                VisibilityState.Visible,
                organizationId,
                workspaceId,
                resourceType,
                resourceId,
                targetParticipantId,
                actorUserProfileId,
                idempotencyKey,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        return outcome.AlreadyApplied
            ? RevealResult.AlreadyApplied(resourceType, resourceId, targetParticipantId)
            : RevealResult.Applied(resourceType, resourceId, targetParticipantId, outcome.VisibilityChanged);
    }

    /// <summary>
    /// Hides (un-reveals) the given resource in the given workspace, idempotently for the given client
    /// idempotency key — the "Reveal Lifecycle" hide command (CORE-REV-001). It takes a reveal back so a
    /// previously visible resource becomes <see cref="VisibilityState.Hidden"/> again in the SAME
    /// dimension the reveal used: an audience-wide hide flips the audience-wide rule, a
    /// selected-participant hide flips only that participant's rule (so the audience and the other
    /// participants are untouched). Returns <see cref="HideOutcome.Applied"/> the first time a key is seen
    /// and <see cref="HideOutcome.AlreadyApplied"/> on a retry; after either the resource is hidden.
    /// Because an absent rule already means hidden, hiding a resource that has no rule (or whose rule is
    /// already hidden) is a no-op that creates nothing and audits nothing.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The session's workspace the resource belongs to.</param>
    /// <param name="resourceType">The kind of resource to hide.</param>
    /// <param name="resourceId">The surrogate id of the resource to hide.</param>
    /// <param name="targetParticipantId">
    /// The participant to hide for (a SELECTED-participant hide, mirroring the selected reveal,
    /// CORE-VIS-005), or <see langword="null"/> to hide from the WHOLE audience. When set it must be a
    /// real participant id; the caller is responsible for having resolved it within the resource's
    /// workspace.
    /// </param>
    /// <param name="actorUserProfileId">
    /// The authenticated user who executed the hide — the audited actor (CORE-VIS-006). Must be non-empty.
    /// </param>
    /// <param name="idempotencyKey">The client <c>Idempotency-Key</c> header value.</param>
    /// <param name="now">The command timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, resource id or actor id is empty, the target participant id is
    /// empty, or the idempotency key is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The resource type is not defined.</exception>
    public async Task<HideResult> HideAsync(
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
        var outcome = await ApplyVisibilityChangeAsync(
                _hideScopePrefix,
                VisibilityState.Hidden,
                organizationId,
                workspaceId,
                resourceType,
                resourceId,
                targetParticipantId,
                actorUserProfileId,
                idempotencyKey,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        return outcome.AlreadyApplied
            ? HideResult.AlreadyApplied(resourceType, resourceId, targetParticipantId)
            : HideResult.Applied(resourceType, resourceId, targetParticipantId, outcome.VisibilityChanged);
    }

    /// <summary>
    /// The shared engine behind <see cref="RevealAsync"/> and <see cref="HideAsync"/>: it validates the
    /// inputs, applies the idempotency retry short-circuit, ensures the resource is in
    /// <paramref name="targetState"/> in the target dimension, audits a real change and records the key —
    /// all identically for both directions, so reveal and hide differ ONLY in their idempotency scope
    /// (<paramref name="scopePrefix"/>) and their target state. Returns the idempotency outcome and
    /// whether the visibility ACTUALLY changed, which the public method maps to its own result type and
    /// the endpoint uses to emit the durable realtime event exactly once per real change.
    /// </summary>
    private async Task<VisibilityCommandOutcome> ApplyVisibilityChangeAsync(
        string scopePrefix,
        VisibilityState targetState,
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
                "Target participant id must not be empty; pass null to apply the change to the whole audience.",
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

        var scope = BuildScope(scopePrefix, organizationId);

        // RETRY SHORT-CIRCUIT: if this key was already recorded, the request was already processed;
        // do not re-apply (no duplicate effect / duplicate event).
        var existing = await _idempotency.FindAsync(scope, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new VisibilityCommandOutcome(AlreadyApplied: true, VisibilityChanged: false);
        }

        // Apply the (state-idempotent) effect, then record the key. Recording after the effect means
        // a crash between the two leaves the key unrecorded, so a retry safely re-ensures the target
        // state (the effect is idempotent) rather than skipping it.
        var change = await EnsureStateAsync(
                organizationId, workspaceId, resourceType, resourceId, targetParticipantId, targetState, now, cancellationToken)
            .ConfigureAwait(false);

        // AUDIT (CORE-VIS-006): append an append-only audit record IFF the visibility actually changed.
        // This sits inside the retry short-circuit (a repeat key returned above) and after the effect, so
        // a retry writes no second record and a no-op (revealing an already-visible, or hiding an
        // already-hidden, resource) records nothing — only a real change is audited
        // (docs/07_SECURITY_THREAT_MODEL.md: "audit creation for visibility changes").
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
        // (the effect it applied is the same idempotent "ensure state"). On a first apply, surface
        // whether the visibility ACTUALLY changed (the same signal the audit used) so the endpoint emits
        // the durable ContentRevealed/ContentHidden event exactly once per real change.
        return recorded == IdempotencyKeyAddResult.Duplicate
            ? new VisibilityCommandOutcome(AlreadyApplied: true, VisibilityChanged: false)
            : new VisibilityCommandOutcome(AlreadyApplied: false, VisibilityChanged: change is not null);
    }

    /// <summary>
    /// Ensures the given resource is in <paramref name="targetState"/> IN THE TARGET DIMENSION,
    /// idempotently, and reports the visibility CHANGE it made (or <see langword="null"/> when nothing
    /// changed) so the caller can audit exactly the real changes (CORE-VIS-006). The target dimension is
    /// either audience-wide (<paramref name="targetParticipantId"/> is <see langword="null"/>) or one
    /// selected participant; the two are independent — a change in one never touches the other, so hiding
    /// a participant's private reveal never affects the audience-wide rule and vice versa. Within the
    /// matching dimension:
    /// <list type="bullet">
    ///   <item>If the resource is already in the target state, does nothing and returns
    ///   <see langword="null"/> (no change to audit). For <see cref="VisibilityState.Visible"/> that means
    ///   a visible rule already exists; for <see cref="VisibilityState.Hidden"/> that means NO visible
    ///   rule exists (an absent rule already means hidden).</item>
    ///   <item>Otherwise it flips the rule standing in the way to the target state (a hidden rule for a
    ///   reveal, the visible rule for a hide) via the CORE-VIS-001 primitive, recording the prior state.</item>
    ///   <item>When no rule exists yet it CREATES one only for a reveal (target Visible); a hide needs no
    ///   rule because absence already means hidden, so the "already hidden" branch above returns
    ///   <see langword="null"/> first.</item>
    /// </list>
    /// All reads/writes are tenant- and workspace-scoped.
    /// </summary>
    private async Task<VisibilityChange?> EnsureStateAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        VisibilityState targetState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rules = await _rules
            .ListByResourceAsync(organizationId, workspaceId, resourceType, resourceId, cancellationToken)
            .ConfigureAwait(false);

        // Only the rules in the SAME target dimension are relevant: audience-wide rules for an
        // audience-wide change, or rules scoped to exactly this participant for a selected change.
        var inDimension = rules
            .Where(rule => rule.TargetParticipantId == targetParticipantId)
            .ToArray();

        var isVisibleNow = inDimension.Any(rule => rule.IsVisibleToAudience());

        // Already in the target state in this dimension: nothing to do (state-level idempotence), nothing
        // to audit. Visible target -> a visible rule already exists; Hidden target -> no visible rule does.
        var alreadyInTargetState = targetState == VisibilityState.Visible ? isVisibleNow : !isVisibleNow;
        if (alreadyInTargetState)
        {
            return null;
        }

        // A rule stands in the way: flip it to the target state (the CORE-VIS-001 primitive) rather than
        // accumulating a second rule. For a reveal that is a hidden rule (the first in the dimension); for
        // a hide it is the visible rule. Capture the prior state BEFORE the change for the audit.
        var ruleToChange = targetState == VisibilityState.Visible
            ? (inDimension.Length > 0 ? inDimension[0] : null)
            : inDimension.FirstOrDefault(rule => rule.IsVisibleToAudience());
        if (ruleToChange is not null)
        {
            var previousVisibility = ruleToChange.Visibility;
            ruleToChange.ChangeVisibility(targetState, now);
            await _rules.UpdateAsync(ruleToChange, cancellationToken).ConfigureAwait(false);
            return new VisibilityChange(previousVisibility, targetState);
        }

        // No rule in this dimension yet. This is reached ONLY for a reveal (a hide with no rule is already
        // hidden and returned null above): create a visible rule — audience-wide or scoped to the
        // participant, per the target. There is no prior state.
        var created = targetParticipantId is { } participantId
            ? VisibilityRule.CreateForParticipant(
                organizationId, workspaceId, resourceType, resourceId, participantId, targetState, now)
            : VisibilityRule.Create(
                organizationId, workspaceId, resourceType, resourceId, targetState, now);
        await _rules.AddAsync(created, cancellationToken).ConfigureAwait(false);
        return new VisibilityChange(PreviousVisibility: null, NewVisibility: targetState);
    }

    /// <summary>
    /// A visibility change applied by <see cref="EnsureStateAsync"/>: the state BEFORE the change
    /// (<see langword="null"/> when no rule existed) and the state AFTER. Used to write the audit record.
    /// </summary>
    private readonly record struct VisibilityChange(
        VisibilityState? PreviousVisibility,
        VisibilityState NewVisibility);

    /// <summary>
    /// The idempotency outcome of a visibility command: whether it was an idempotent retry
    /// (<see cref="AlreadyApplied"/>) and, for a first apply, whether it ACTUALLY changed the visibility
    /// (<see cref="VisibilityChanged"/>, the signal the audit and the durable realtime event use).
    /// </summary>
    private readonly record struct VisibilityCommandOutcome(bool AlreadyApplied, bool VisibilityChanged);

    /// <summary>Idempotency scope prefix for reveal commands.</summary>
    private const string _revealScopePrefix = "reveal";

    /// <summary>Idempotency scope prefix for hide commands; distinct so a hide key never collides with a reveal key.</summary>
    private const string _hideScopePrefix = "hide";

    /// <summary>
    /// Builds the per-tenant, per-operation idempotency scope for a visibility command, so a client key is
    /// idempotent within a tenant's reveals (or hides) and never collides across tenants (threat T5) or
    /// across the two operations.
    /// </summary>
    private static string BuildScope(string scopePrefix, Guid organizationId) => $"{scopePrefix}:{organizationId}";
}
