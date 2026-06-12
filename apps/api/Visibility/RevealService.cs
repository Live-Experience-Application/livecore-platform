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
/// and workspace-scoped (organization boundary before workspace boundary; threat T5). Audience-wide
/// reveal only: revealing to a SELECTED subset of participants is the later CORE-VIS-005, and the
/// durable <c>ContentRevealed</c>/<c>VisibilityRuleChanged</c> session event (docs/09_EVENT_CATALOG.md)
/// is deferred to the Realtime epic (CORE-RT-003), exactly as CORE-SES-004 deferred
/// <c>SessionStarted</c>/<c>SessionEnded</c> — there is no event store/transport yet. The
/// same-workspace coupling of the target resource (that the resource id refers to a real resource in
/// the workspace) is the documented carry-over deferred to a resource-resolution step, mirroring
/// <c>ContentBlock.SceneId</c> / <c>Entity.EntityTypeId</c>; this command sets the rule by (type, id)
/// and does not yet resolve the resource.
/// </summary>
internal sealed class RevealService
{
    private readonly IVisibilityRuleRepository _rules;
    private readonly IIdempotencyKeyStore _idempotency;

    public RevealService(IVisibilityRuleRepository rules, IIdempotencyKeyStore idempotency)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(idempotency);
        _rules = rules;
        _idempotency = idempotency;
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
    /// <param name="idempotencyKey">The client <c>Idempotency-Key</c> header value.</param>
    /// <param name="now">The command timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or resource id is empty, or the idempotency key is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The resource type is not defined.</exception>
    public async Task<RevealResult> RevealAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
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
            return RevealResult.AlreadyApplied(resourceType, resourceId);
        }

        // Apply the (state-idempotent) effect, then record the key. Recording after the effect means
        // a crash between the two leaves the key unrecorded, so a retry safely re-ensures visibility
        // (the effect is idempotent) rather than skipping it.
        await EnsureVisibleAsync(organizationId, workspaceId, resourceType, resourceId, now, cancellationToken)
            .ConfigureAwait(false);

        var recorded = await _idempotency
            .AddAsync(IdempotencyKey.Create(scope, idempotencyKey, now), cancellationToken)
            .ConfigureAwait(false);

        // A concurrent request recorded the same key first: report this one as an idempotent retry
        // (the effect it applied is the same idempotent "ensure visible").
        return recorded == IdempotencyKeyAddResult.Duplicate
            ? RevealResult.AlreadyApplied(resourceType, resourceId)
            : RevealResult.Applied(resourceType, resourceId);
    }

    /// <summary>
    /// Ensures the given resource is visible to the audience, idempotently: if a visibility rule
    /// already makes it visible, does nothing; otherwise flips an existing (hidden) rule to visible,
    /// or creates a visible rule when the resource has none. All reads/writes are tenant- and
    /// workspace-scoped.
    /// </summary>
    private async Task EnsureVisibleAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rules = await _rules
            .ListByResourceAsync(organizationId, workspaceId, resourceType, resourceId, cancellationToken)
            .ConfigureAwait(false);

        // Already visible: nothing to do (state-level idempotence).
        if (rules.Any(rule => rule.IsVisibleToAudience()))
        {
            return;
        }

        // A hidden rule exists for the resource: flip it to visible (the CORE-VIS-001 primitive)
        // rather than accumulating a second rule.
        var hiddenRule = rules.Count > 0 ? rules[0] : null;
        if (hiddenRule is not null)
        {
            hiddenRule.ChangeVisibility(VisibilityState.Visible, now);
            await _rules.UpdateAsync(hiddenRule, cancellationToken).ConfigureAwait(false);
            return;
        }

        // No rule for the resource yet: create a visible one.
        var created = VisibilityRule.Create(
            organizationId, workspaceId, resourceType, resourceId, VisibilityState.Visible, now);
        await _rules.AddAsync(created, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the per-tenant idempotency scope for reveal commands, so a client key is idempotent
    /// within a tenant's reveals and never collides across tenants (threat T5).
    /// </summary>
    private static string BuildScope(Guid organizationId) => $"reveal:{organizationId}";
}
