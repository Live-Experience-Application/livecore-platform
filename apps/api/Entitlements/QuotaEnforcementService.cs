// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Server-side quota ENFORCEMENT for protected workspace and session commands (CORE-ENTL-004, the quota enforcement
/// story of the "Entitlements and Quotas" epic). CORE-ENTL-003 modeled the per-subject quota usage and the
/// server-side <see cref="QuotaStatus"/> calculation, and explicitly deferred "INCREMENTING this amount as protected
/// workspace/session commands run, and ENFORCING the limit at those commands" to this story; this service is that
/// behavior.
///
/// THE EPIC ACCEPTANCE CRITERION — "Free limits cannot be bypassed by clients". A protected command asks
/// <see cref="TryConsumeAsync"/> to ATOMICALLY check-and-consume the requested amount of a subject's quota: the
/// allow/deny decision and the increment are one limit-guarded statement, so the command consumes a unit only when
/// it fits the cap (a command that releases a counted resource — a session ending — calls <see cref="ReleaseAsync"/>).
/// The decision is computed entirely server-side from the catalog limit and the recorded usage; the client supplies
/// no part of it, so a client can never raise its own limit (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md:
/// "Never trust client-side premium flags").
///
/// ATOMIC CHECK-AND-CONSUME — NO TOCTOU RACE (CORE-CONC-004). The check and the consume are a SINGLE atomic
/// limit-guarded increment in the database (<see cref="IQuotaUsageRepository.TryConsumeAsync"/>:
/// <c>UPDATE ... SET used = used + amount WHERE ... AND used + amount &lt;= limit</c>), never a read-then-write that
/// two concurrent commands could both pass. So N concurrent commands consuming the same quota at a limit of one
/// yield exactly one success and N-1 quota-exceeded — <c>session.active.max</c> / <c>workspace.active.max</c> can
/// never be exceeded under a race. This supersedes the earlier separate check-then-record steps, which had no row
/// lock or reserved increment.
///
/// FAIL-CLOSED AND CONSISTENT WITH THE STATUS READ. The reported decision reuses the SINGLE quota math
/// (<see cref="QuotaStatus.Calculate"/>) the <c>GET /api/v1/.../quota-status</c> endpoints use, so a command's
/// allow/deny can never diverge from what a client sees: a subject not entitled to a defined quota has no allowance
/// (limit treated as zero), and an unlimited (fair-use) grant always has headroom. Core enforces only quotas that
/// EXIST: when no active <see cref="QuotaDefinition"/> governs the command for the subject kind, there is nothing to
/// enforce, the command proceeds and nothing is recorded — a deployment that wants a free limit defines the quota
/// and grants the free entitlement.
///
/// The service is keyed purely by the (subject type, subject id) pair and a generic quota key; it does not itself
/// authorize the caller. A protected command authorizes the caller server-side (role + tenant + workspace) FIRST and
/// asks this service only once the caller is allowed to run the command, exactly as the quota-status endpoints
/// authorize the subject before calling the calculator. The atomic consume runs through the command's shared
/// <see cref="LiveCore.Api.Persistence.LiveCoreDbContext"/>, so when a command wraps its writes in a transaction
/// (the session lifecycle commands) the consume commits or rolls back with the rest of the command.
/// </summary>
public sealed class QuotaEnforcementService
{
    private readonly IQuotaDefinitionRepository _quotaDefinitions;
    private readonly SubjectEntitlementResolver _entitlementResolver;
    private readonly IQuotaUsageRepository _quotaUsage;
    private readonly TimeProvider _timeProvider;

    public QuotaEnforcementService(
        IQuotaDefinitionRepository quotaDefinitions,
        SubjectEntitlementResolver entitlementResolver,
        IQuotaUsageRepository quotaUsage,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(quotaDefinitions);
        ArgumentNullException.ThrowIfNull(entitlementResolver);
        ArgumentNullException.ThrowIfNull(quotaUsage);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _quotaDefinitions = quotaDefinitions;
        _entitlementResolver = entitlementResolver;
        _quotaUsage = quotaUsage;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// ATOMICALLY checks-and-consumes <paramref name="amount"/> of the quota named by <paramref name="entitlementKey"/>
    /// for the subject, and returns the resulting decision (CORE-CONC-004). The check (does it fit the granted
    /// limit?) and the consume (the increment) are a SINGLE limit-guarded statement, so two concurrent commands can
    /// never both pass the limit: when this returns <see cref="QuotaEnforcementDecision.IsAllowed"/> =
    /// <see langword="true"/> the unit HAS been consumed, and when it returns <see langword="false"/> nothing was
    /// consumed. The command proceeds only on an allowed decision. It is fail-closed: a subject not entitled to a
    /// defined quota has zero allowance and is denied with nothing consumed; an unlimited (fair-use) grant always
    /// consumes; an ungoverned command (no active definition) is allowed and nothing is recorded.
    /// </summary>
    /// <param name="subjectType">The kind of subject the quota is measured for.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="entitlementKey">The generic quota entitlement key the command consumes (see <see cref="QuotaEntitlementKeys"/>).</param>
    /// <param name="amount">The amount the command consumes (strictly positive).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The subject id is empty or the key is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The amount is not strictly positive.</exception>
    public async Task<QuotaEnforcementDecision> TryConsumeAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        string entitlementKey,
        long amount,
        CancellationToken cancellationToken)
    {
        ValidateArguments(subjectId, entitlementKey, amount);

        var definition = await FindActiveDefinitionAsync(subjectType, entitlementKey, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            // No active quota governs this command in this deployment: there is nothing to enforce. Core enforces
            // only quotas that exist (a deployment that wants a free limit defines the quota and grants the
            // entitlement), so the command proceeds and nothing is recorded.
            return QuotaEnforcementDecision.NotGoverned(entitlementKey);
        }

        // Resolve the granted limit from the subject's ACTIVE entitlements (a revoked grant immediately removes the
        // headroom). A subject not entitled to the defined quota has NO allowance (fail-closed), so deny without
        // consuming anything.
        var entitlements = await _entitlementResolver
            .ResolveAsync(subjectType, subjectId, cancellationToken)
            .ConfigureAwait(false);
        if (!entitlements.TryGetQuotaLimit(definition.EntitlementKey, out var limit))
        {
            var deniedStatus = QuotaStatus.Calculate(
                definition.EntitlementKey, definition.Unit, isEntitled: false, limit: null, used: 0);
            return QuotaEnforcementDecision.Denied(deniedStatus);
        }

        // ATOMIC check-and-consume: the limit-guarded increment is a single statement, so a concurrent command
        // re-evaluates the cap against the just-committed row and is rejected rather than overrunning it.
        var now = _timeProvider.GetUtcNow();
        var outcome = await _quotaUsage
            .TryConsumeAsync(subjectType, subjectId, definition, amount, limit, now, cancellationToken)
            .ConfigureAwait(false);

        // Report the decision over the SAME quota math the status read uses, from the post-consume usage, so an
        // allow/deny and what a client sees can never diverge.
        var used = await _quotaUsage
            .GetUsedAmountAsync(subjectType, subjectId, definition.Id, cancellationToken)
            .ConfigureAwait(false);
        var status = QuotaStatus.Calculate(definition.EntitlementKey, definition.Unit, isEntitled: true, limit, used);
        return outcome == QuotaUsageConsumeResult.Consumed
            ? QuotaEnforcementDecision.Allowed(status)
            : QuotaEnforcementDecision.Denied(status);
    }

    /// <summary>
    /// READ-ONLY pre-flight check of whether the subject COULD consume <paramref name="amount"/> of the quota named
    /// by <paramref name="entitlementKey"/>, WITHOUT changing any state. Unlike <see cref="TryConsumeAsync"/> this
    /// records nothing, so it carries no atomicity guarantee and must NOT be used to gate a consuming command (two
    /// callers could both pass it). Its only use is an advisory gate on a command that does NOT itself consume the
    /// quota — for example creating a (Prepared) session is blocked while the workspace already runs its maximum
    /// number of LIVE sessions, but the live count is consumed at start, not create. The decision is fail-closed: a
    /// subject not entitled to a defined quota has zero allowance; an unlimited grant has headroom; an ungoverned
    /// command (no active definition) is allowed.
    /// </summary>
    /// <exception cref="ArgumentException">The subject id is empty or the key is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The amount is not strictly positive.</exception>
    public async Task<QuotaEnforcementDecision> CheckAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        string entitlementKey,
        long amount,
        CancellationToken cancellationToken)
    {
        ValidateArguments(subjectId, entitlementKey, amount);

        var definition = await FindActiveDefinitionAsync(subjectType, entitlementKey, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return QuotaEnforcementDecision.NotGoverned(entitlementKey);
        }

        var entitlements = await _entitlementResolver
            .ResolveAsync(subjectType, subjectId, cancellationToken)
            .ConfigureAwait(false);
        var isEntitled = entitlements.TryGetQuotaLimit(definition.EntitlementKey, out var limit);

        var used = await _quotaUsage
            .GetUsedAmountAsync(subjectType, subjectId, definition.Id, cancellationToken)
            .ConfigureAwait(false);
        var status = QuotaStatus.Calculate(definition.EntitlementKey, definition.Unit, isEntitled, limit, used);

        // Allowed iff the requested amount fits the remaining headroom. An unlimited grant has null remaining
        // (treated as infinite); a not-entitled subject has zero remaining (fail-closed); a capped grant has
        // max(0, limit - used).
        return (status.Remaining ?? long.MaxValue) >= amount
            ? QuotaEnforcementDecision.Allowed(status)
            : QuotaEnforcementDecision.Denied(status);
    }

    /// <summary>
    /// Releases <paramref name="amount"/> of the subject's recorded usage of the quota named by
    /// <paramref name="entitlementKey"/>, by decrementing its recorded usage (clamped at zero so it never goes
    /// negative). Called by a command that frees a counted resource — a session ending frees its workspace's active
    /// session slot — so an "active" quota reflects the current count rather than a lifetime total. A no-op when no
    /// usage is recorded (nothing to release) or when no active quota governs the command.
    /// </summary>
    /// <exception cref="ArgumentException">The subject id is empty or the key is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The amount is not strictly positive.</exception>
    public async Task ReleaseAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        string entitlementKey,
        long amount,
        CancellationToken cancellationToken)
    {
        ValidateArguments(subjectId, entitlementKey, amount);

        var definition = await FindActiveDefinitionAsync(subjectType, entitlementKey, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return;
        }

        var existing = await _quotaUsage
            .FindBySubjectAndQuotaAsync(subjectType, subjectId, definition.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            // Nothing recorded (for example a resource created before enforcement existed): nothing to release.
            return;
        }

        var released = Math.Max(0, existing.UsedAmount - amount);
        if (released == existing.UsedAmount)
        {
            // Already at the floor: no change, so avoid a redundant write.
            return;
        }

        var now = _timeProvider.GetUtcNow();
        existing.Record(released, now);
        await _quotaUsage.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the single ACTIVE quota definition that measures the given key for the given subject kind, or
    /// <see langword="null"/> when none governs it. A quota measures at most one entitlement, so at most one active
    /// definition matches; the read reuses the existing active-by-subject-kind catalog read and matches the key
    /// exactly (the stored keys are canonical).
    /// </summary>
    private async Task<QuotaDefinition?> FindActiveDefinitionAsync(
        EntitlementSubjectType subjectType,
        string entitlementKey,
        CancellationToken cancellationToken)
    {
        var definitions = await _quotaDefinitions
            .ListActiveBySubjectTypeAsync(subjectType, cancellationToken)
            .ConfigureAwait(false);

        foreach (var definition in definitions)
        {
            if (string.Equals(definition.EntitlementKey, entitlementKey, StringComparison.Ordinal))
            {
                return definition;
            }
        }

        return null;
    }

    private static void ValidateArguments(Guid subjectId, string entitlementKey, long amount)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        if (!EntitlementDefinition.IsValidKey(entitlementKey))
        {
            throw new ArgumentException("Entitlement key violates the entitlement key invariants.", nameof(entitlementKey));
        }

        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A consumed amount must be strictly positive.");
        }
    }
}
