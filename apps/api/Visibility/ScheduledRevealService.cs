// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Visibility;

/// <summary>
/// The background scheduled-reveal sweep's application service (CORE-VSEAL-002, the "Scheduled and Sealed
/// Visibility" epic, raised by a vertical adopter, ARC-GAP-002). On the worker's configurable cadence it finds
/// every Hidden visibility rule whose <see cref="VisibilityRule.ScheduledRevealAt"/> time has arrived and
/// AUTOMATICALLY reveals it — the server-enforced "control visibility WHEN" half of the product vision
/// (docs/01_PRODUCT_VISION_AND_SCOPE.md). The scheduling host — the worker — invokes this on an interval
/// (docs/02_ARCHITECTURE.md: the worker owns async jobs), exactly as it invokes the recap-generation service;
/// the service itself is host-agnostic and fully unit-testable.
///
/// <para>
/// DRIVES THE SAME CENTRAL REVEAL COMMAND — NEVER A DUPLICATED REVEAL PATH (the headline requirement). Each
/// auto-reveal goes through the SHARED <see cref="RevealService.RevealAsync"/> — the central Visibility engine —
/// exactly as a live host reveal does, so the reveal is gated through the engine: the rule's binary Hidden state
/// flips to Visible only through that one command, the audit fact is appended by it, and the SAME session events
/// a live reveal emits are composed by the SHARED <see cref="RevealSessionEvents.Compose"/> and carry the revealed
/// resource as their VISIBILITY SUBJECT, so the recipient resolver delivers them to EXACTLY the authorized audience
/// — it never reveals to an unauthorized participant, and a selected-participant scheduled rule reveals only to
/// that participant (threats T2/T3/T5 in docs/07_SECURITY_THREAT_MODEL.md). The auto-reveal is a SYSTEM action
/// (no live user), so it passes a <see langword="null"/> actor: the audit fact and the session events are recorded
/// with no actor, exactly as the recap/retention background jobs record a system action.
/// </para>
///
/// <para>
/// IDEMPOTENT (a rule is auto-revealed AT MOST ONCE) AND TENANT-SAFE (the acceptance criteria). Two layers
/// guarantee at-most-once: (1) the due read returns only HIDDEN rules, so a rule this sweep just revealed (now
/// Visible) is excluded from every later sweep; (2) the auto-reveal uses a DETERMINISTIC per-rule idempotency key
/// (<c>scheduled-reveal:{ruleId}</c>) in the central command's own per-tenant reveal scope, so two overlapping
/// sweeps — in the same process or across worker replicas — or a rule that is manually hidden again after firing
/// can never produce a second reveal or a second set of events (the losing/repeat attempt short-circuits as an
/// idempotent retry). Tenant safety: the cross-tenant due read returns each rule's OWN tenant/workspace/session,
/// and the reveal is driven scoped to exactly those, so a rule in one tenant is only ever auto-revealed within its
/// own tenant — never another's (threat T5). The scheduled time is left UNCHANGED by the reveal, so the projection
/// can still render the WHEN; the binary state flip is what makes it stop being due.
/// </para>
///
/// <para>
/// DURABLE EMIT, REPLAY-GATED DELIVERY. Each sweep runs the reveal AND appends the durable session events inside
/// ONE <see cref="TransactionalUnitOfWork"/> (CORE-CONC-002), so the rule flip, the audit fact, the idempotency
/// key and the events commit together or roll back together. The worker process has no realtime connections, so —
/// exactly like the recap-generation job — it APPENDS the durable events (the source of truth) and relies on the
/// recipient-gated reconnect replay (CORE-RT-005) to deliver them; the append carries the visibility subject, so
/// whether delivered live on a peer API replay or on reconnect, the recipient resolver gates it to the authorized
/// audience only. The sweep is per-rule RESILIENT: a transient failure on one rule is logged (identifier/counts
/// only; threat T7) and that rule is left due for the next sweep, rather than aborting the whole run.
/// </para>
/// </summary>
public sealed class ScheduledRevealService
{
    /// <summary>The deterministic idempotency-key prefix for a scheduled auto-reveal (per-rule, at-most-once).</summary>
    private const string _idempotencyKeyPrefix = "scheduled-reveal";

    private readonly IScheduledRevealDueReader _dueRules;
    private readonly RevealService _reveal;
    private readonly ISessionEventRepository _sessionEvents;
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly ScheduledRevealOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduledRevealService> _logger;

    /// <summary>
    /// Creates the sweep service over the due-rule reader, the central reveal command, the append-only
    /// session-event stream (the durable emit), the unit of work, the policy and the clock.
    /// </summary>
    public ScheduledRevealService(
        IScheduledRevealDueReader dueRules,
        RevealService reveal,
        ISessionEventRepository sessionEvents,
        TransactionalUnitOfWork unitOfWork,
        ScheduledRevealOptions options,
        TimeProvider timeProvider,
        ILogger<ScheduledRevealService> logger)
    {
        ArgumentNullException.ThrowIfNull(dueRules);
        ArgumentNullException.ThrowIfNull(reveal);
        ArgumentNullException.ThrowIfNull(sessionEvents);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _dueRules = dueRules;
        _reveal = reveal;
        _sessionEvents = sessionEvents;
        _unitOfWork = unitOfWork;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one scheduled-reveal sweep: finds up to <see cref="ScheduledRevealOptions.BatchSize"/> rules due for
    /// an automatic reveal as of now, drives the central reveal command for each (a SYSTEM reveal), and returns
    /// the run's count-only summary. Idempotent (a rule is auto-revealed at most once) and tenant-safe (each
    /// reveal is driven scoped to its rule's own tenant); the sweep is per-rule resilient.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <returns>A summary of how many due rules were examined, revealed, idempotently skipped and failed.</returns>
    public async Task<ScheduledRevealResult> RevealDueRulesAsync(CancellationToken cancellationToken)
    {
        // One sweep timestamp from the injected clock, so behavior is deterministic under test and a rule is
        // due iff its scheduled time is at or before it (the "scheduled in the past or AT the sweep time is
        // auto-revealed" criterion).
        var now = _timeProvider.GetUtcNow();

        var due = await _dueRules
            .ListDueRulesAsync(now, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (due.Count == 0)
        {
            return new ScheduledRevealResult(Examined: 0, Revealed: 0, AlreadyApplied: 0, Failed: 0);
        }

        var examined = 0;
        var revealed = 0;
        var alreadyApplied = 0;
        var failed = 0;

        foreach (var rule in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            try
            {
                var outcome = await AutoRevealAsync(rule, now, cancellationToken).ConfigureAwait(false);
                if (outcome.Revealed)
                {
                    revealed++;
                    _logger.LogInformation(
                        "Auto-revealed scheduled visibility rule {RuleId} for resource {ResourceId} in session {SessionId}.",
                        rule.RuleId,
                        rule.ResourceId,
                        rule.SessionId);
                }
                else
                {
                    // The deterministic reveal key was already recorded (a concurrent sweep or another worker
                    // replica revealed it, or the rule was manually hidden again after firing): a benign no-op,
                    // never a duplicate reveal (the at-most-once backstop).
                    alreadyApplied++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown during the sweep is expected; stop cleanly and let the loop end.
                throw;
            }
            catch (DbUpdateException exception)
            {
                // The rule/session/tenant may have been removed between the due read and the reveal (a
                // foreign-key violation), or a transient persistence error occurred. The rule stays Hidden (no
                // reveal committed), so it remains due for the next sweep; log identifiers/counts only (threat
                // T7) and move on rather than aborting the run.
                failed++;
                _logger.LogWarning(
                    exception,
                    "Failed to auto-reveal scheduled visibility rule {RuleId}; it stays due and will be retried on the next sweep.",
                    rule.RuleId);
            }
        }

        return new ScheduledRevealResult(
            Examined: examined, Revealed: revealed, AlreadyApplied: alreadyApplied, Failed: failed);
    }

    /// <summary>
    /// Auto-reveals ONE due rule through the central reveal command, inside a single unit of work, and appends
    /// the durable session events on a real change. Returns whether the reveal ACTUALLY changed the visibility
    /// (a fresh auto-reveal) or was an idempotent no-op.
    /// </summary>
    private async Task<(bool Revealed, bool AlreadyApplied)> AutoRevealAsync(
        DueScheduledReveal rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Deterministic per-rule key, so the auto-reveal is at-most-once across concurrent sweeps / replicas and
        // across a manual re-hide of an already-fired rule (the central command's idempotency store records it
        // in the per-tenant reveal scope).
        var idempotencyKey = string.Create(
            CultureInfo.InvariantCulture, $"{_idempotencyKeyPrefix}:{rule.RuleId}");

        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // Drive the SAME central reveal command as a live host reveal, but as a SYSTEM action (null
                // actor). It flips the rule Hidden -> Visible (or short-circuits as an idempotent retry),
                // appends the audit fact and records the key — all through the central Visibility engine.
                var result = await _reveal
                    .RevealAsync(
                        rule.OrganizationId,
                        rule.WorkspaceId,
                        rule.SessionId,
                        rule.ResourceType,
                        rule.ResourceId,
                        rule.TargetParticipantId,
                        actorUserProfileId: null,
                        idempotencyKey,
                        now,
                        transactionCancellationToken)
                    .ConfigureAwait(false);

                // A sealed rule is excluded by the due read, so BlockedByLock is not expected here; if a rule was
                // sealed between the read and the reveal, the command refuses it fail-closed — treat it as a
                // no-op (not revealed), exactly as the central engine intends.
                if (result.BlockedByLock)
                {
                    return (Revealed: false, AlreadyApplied: false);
                }

                if (!result.VisibilityChanged)
                {
                    return (Revealed: false, AlreadyApplied: true);
                }

                // A real auto-reveal: emit the SAME durable session events a live reveal emits (the SHARED
                // composer), as a SYSTEM event (null actor). They are appended inside this transaction (the
                // durable emit); recipient-gated delivery happens on reconnect replay (the worker has no realtime
                // connections), exactly as the recap-generation job emits its event.
                var events = RevealSessionEvents.Compose(
                    rule.OrganizationId,
                    rule.WorkspaceId,
                    rule.SessionId,
                    actorUserProfileId: null,
                    rule.TargetParticipantId,
                    rule.ResourceType,
                    rule.ResourceId,
                    now);

                foreach (var sessionEvent in events)
                {
                    await _sessionEvents.AppendAsync(sessionEvent, transactionCancellationToken).ConfigureAwait(false);
                }

                return (Revealed: true, AlreadyApplied: false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
