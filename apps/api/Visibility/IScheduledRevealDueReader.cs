// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// Finds the visibility rules that are DUE for an automatic scheduled reveal (CORE-VSEAL-002) — the read the
/// worker's background sweep (<see cref="ScheduledRevealService"/>) issues each tick to discover which Hidden,
/// scheduled rules have reached their <see cref="VisibilityRule.ScheduledRevealAt"/> time.
///
/// This is a SYSTEM read that spans ALL tenants ON PURPOSE: the sweep is a background maintenance job, not a
/// tenant actor, exactly like the recap-generation eligibility read (<c>IRecapEligibleSessionReader</c>) and the
/// data-retention sweep. It is therefore deliberately SEPARATE from the tenant-scoped
/// <see cref="IVisibilityRuleRepository"/> (which has "no lookup that crosses tenants, and NO list-everything
/// method"), so the cross-tenant query lives outside that security contract. Tenant SAFETY is preserved by the
/// CONSUMER: each returned <see cref="DueScheduledReveal"/> carries its OWN rule's organization, workspace and
/// session, and the sweep drives the central reveal command scoped to exactly those, so a rule in one tenant is
/// only ever auto-revealed within its own tenant/workspace/session — never another's (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public interface IScheduledRevealDueReader
{
    /// <summary>
    /// Lists up to <paramref name="maxCount"/> rules that are due for an automatic reveal AS OF
    /// <paramref name="asOf"/> — HIDDEN rules that carry a <see cref="VisibilityRule.ScheduledRevealAt"/> at or
    /// before <paramref name="asOf"/> and are NOT sealed (a locked rule's visibility cannot change, so a sealed
    /// scheduled rule is never auto-revealed; the reveal command would refuse it anyway). The page is bounded so
    /// one sweep can never do unbounded work (threat T9), ordered by the time-ordered surrogate id so the oldest
    /// due rules surface first and repeated sweeps cover the rest. A rule that has ALREADY been auto-revealed is
    /// no longer Hidden, so it naturally leaves this candidate set (the sweep's primary at-most-once mechanism).
    /// </summary>
    /// <param name="asOf">The sweep time; a rule is due when its scheduled reveal time is at or before this.</param>
    /// <param name="maxCount">The maximum number of due rules to return (the sweep batch size; must be positive).</param>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is not positive.</exception>
    Task<IReadOnlyList<DueScheduledReveal>> ListDueRulesAsync(
        DateTimeOffset asOf,
        int maxCount,
        CancellationToken cancellationToken);
}

/// <summary>
/// The coordinates of one rule that is due for an automatic scheduled reveal (CORE-VSEAL-002): everything the
/// worker sweep needs to drive the central reveal command for it and to derive a deterministic idempotency key —
/// the rule's tenant/workspace/session, the governed resource, the optional selected-participant target and the
/// rule id. It is a read PROJECTION (not the tracked aggregate), exactly like <c>RecapEligibleSession</c>, so the
/// sweep is decoupled from the rule's change-tracking. Every field is an identifier — never content (threat T7).
/// </summary>
/// <param name="RuleId">The rule's surrogate id (the deterministic idempotency-key seed for the auto-reveal).</param>
/// <param name="OrganizationId">The tenant that owns the rule (the auto-reveal is driven scoped to this).</param>
/// <param name="WorkspaceId">The rule's workspace.</param>
/// <param name="SessionId">The rule's session (the reveal is session-scoped, CORE-SVIS-001).</param>
/// <param name="ResourceType">The kind of resource the rule governs.</param>
/// <param name="ResourceId">The surrogate id of the resource the rule governs.</param>
/// <param name="TargetParticipantId">
/// The selected participant the rule is scoped to, or <see langword="null"/> for an audience-wide rule — so the
/// auto-reveal reveals in exactly the rule's own dimension (a selected-participant reveal never widens to the
/// audience; threat T5).
/// </param>
public sealed record DueScheduledReveal(
    Guid RuleId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid SessionId,
    VisibilityResourceType ResourceType,
    Guid ResourceId,
    Guid? TargetParticipantId);
