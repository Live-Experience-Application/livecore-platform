// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Visibility;

/// <summary>
/// The visibility-rule LOCK command of the Visibility module (CORE-VSEAL-001, the "Scheduled and Sealed
/// Visibility" epic, raised by a vertical adopter, ARC-GAP-002). An authoring role SEALS (locks) or unseals
/// (unlocks) an existing <see cref="VisibilityRule"/>, asserting a server-side flag that makes the governed
/// resource permanently-restricted: while a rule is locked, a reveal/hide/visibility change targeting it is
/// refused fail-closed (the <see cref="RevealService"/> resolves the locked rule and returns the 409 outcome).
///
/// This service is the authorized command's EFFECT: the endpoint performs authentication, tenant resolution
/// and the authoring-role authorization before calling in, exactly as the create command splits its endpoint
/// from <see cref="VisibilityRuleService"/>. It REUSES the Visibility domain — the CORE-VIS-001 aggregate's
/// <see cref="VisibilityRule.Lock"/>/<see cref="VisibilityRule.Unlock"/> transitions and the module's
/// <see cref="IVisibilityRuleRepository"/> — and the existing audit pipeline; it builds no parallel rule logic.
///
/// ORTHOGONAL TO THE BINARY STATE. Locking changes ONLY the lock flag, never the binary Hidden/Visible
/// <see cref="VisibilityState"/> (it is NOT a third state), so the recipient resolver and every projection
/// that branches on the binary state are unchanged for an unlocked rule, and the binary enforcement of a
/// locked rule is exactly whatever Hidden/Visible state it already carries — only further CHANGES to that
/// state are now barred.
///
/// SESSION-SCOPED, FAIL-CLOSED. The rule is loaded through the tenant-, workspace- AND session-scoped
/// <see cref="IVisibilityRuleRepository.FindByIdInSessionAsync"/>, so a rule of another session, workspace or
/// tenant is never reachable through this session's route even when the surrogate id is known; an
/// unknown/foreign/cross-session rule is reported as "not found" (the endpoint hides it as 404; threats
/// T1/T5/T3).
///
/// AUDIT + ATOMICITY. A lock change is security-relevant, so a REAL change appends an append-only
/// <see cref="AuditAction.VisibilityRuleLockChanged"/> record through the audit pipeline
/// (<see cref="AuditLogEntry.ForVisibilityRuleLockChange"/>): the authoring actor, the governed resource, the
/// optional selected-participant target and the before/after lock-state names. The rule update and the audit
/// append run inside ONE <see cref="TransactionalUnitOfWork"/> (CORE-CONC-002), so either both commit or a
/// failure rolls both back. The command is IDEMPOTENT: locking an already-locked rule (or unlocking an
/// already-unlocked one) changes nothing, audits nothing and still succeeds, returning the rule unchanged.
/// The audit row carries only identifiers, an enum and a generic state name — never resolved content (threat
/// T7).
/// </summary>
internal sealed class VisibilityRuleLockService
{
    /// <summary>The generic lock-state names recorded in the audit before/after columns (never enum values).</summary>
    private const string _lockedStateName = "Locked";
    private const string _unlockedStateName = "Unlocked";

    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IVisibilityRuleRepository _rules;
    private readonly IAuditLogRepository _audit;

    public VisibilityRuleLockService(
        TransactionalUnitOfWork unitOfWork,
        IVisibilityRuleRepository rules,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _rules = rules;
        _audit = audit;
    }

    /// <summary>
    /// Sets (or clears) the seal on the visibility rule identified by <paramref name="ruleId"/> WITHIN the
    /// given tenant, workspace and session, atomically auditing a real change. Returns the (possibly mutated)
    /// rule on success, or <see langword="null"/> when no such rule exists in this (organization, workspace,
    /// session) — the endpoint hides that as 404 (fail-closed). Idempotent: a no-op lock/unlock returns the
    /// rule unchanged and audits nothing.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The session's workspace the rule belongs to.</param>
    /// <param name="sessionId">The session the rule is scoped to (a reveal is session-scoped, CORE-SVIS-001).</param>
    /// <param name="ruleId">The surrogate id of the visibility rule to seal/unseal.</param>
    /// <param name="locked">Whether to SEAL (<see langword="true"/>) or unseal (<see langword="false"/>) the rule.</param>
    /// <param name="actorUserProfileId">
    /// The authenticated authoring role who changed the lock — the audited actor. Supplied by the endpoint
    /// from the resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the rule's update time and the audit record's time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The organization, workspace, session, rule or actor id is empty.</exception>
    public async Task<VisibilityRule?> SetLockAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid ruleId,
        bool locked,
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

        if (ruleId == Guid.Empty)
        {
            throw new ArgumentException("Visibility rule id must not be empty.", nameof(ruleId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // SESSION-SCOPED load (CORE-SVIS-001): the lookup leads with organization_id then workspace_id then
        // session_id then the rule id, so a rule of another tenant, workspace or session is never returned
        // even when the surrogate id matches; an unknown rule yields null (hidden 404; threats T1/T5/T3).
        var rule = await _rules
            .FindByIdInSessionAsync(organizationId, workspaceId, sessionId, ruleId, cancellationToken)
            .ConfigureAwait(false);
        if (rule is null)
        {
            return null;
        }

        // ONE unit of work (CORE-CONC-002): the rule update and the audit append commit together or roll back
        // together. The transaction is opened inside the EF execution strategy, so it stays correct under the
        // CORE-CONC-003 retrying strategy. A no-op (already in the requested lock state) changes nothing and
        // audits nothing — the command is idempotent.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                var changed = locked ? rule.Lock(now) : rule.Unlock(now);
                if (changed)
                {
                    await _rules.UpdateAsync(rule, transactionCancellationToken).ConfigureAwait(false);

                    // AUDIT (security-relevant): record the orthogonal lock transition by its before/after
                    // lock-state names (the inverse pair, since we only reach here on a real change). The
                    // record carries only identifiers, an enum and the generic state names — never resolved
                    // content (threat T7).
                    var newState = locked ? _lockedStateName : _unlockedStateName;
                    var previousState = locked ? _unlockedStateName : _lockedStateName;
                    var entry = AuditLogEntry.ForVisibilityRuleLockChange(
                        organizationId,
                        workspaceId,
                        actorUserProfileId,
                        rule.ResourceType.ToString(),
                        rule.ResourceId,
                        rule.TargetParticipantId,
                        previousState,
                        newState,
                        now);
                    await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);
                }

                return rule;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
