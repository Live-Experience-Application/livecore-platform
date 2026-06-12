namespace LiveCore.Api.Entitlements;

/// <summary>
/// Server-side quota ENFORCEMENT for protected workspace and session commands (CORE-ENTL-004, the quota enforcement
/// story of the "Entitlements and Quotas" epic). CORE-ENTL-003 modeled the per-subject quota usage and the
/// server-side <see cref="QuotaStatus"/> calculation, and explicitly deferred "INCREMENTING this amount as protected
/// workspace/session commands run, and ENFORCING the limit at those commands" to this story; this service is that
/// behavior.
///
/// THE EPIC ACCEPTANCE CRITERION — "Free limits cannot be bypassed by clients". A protected command asks
/// <see cref="CheckAsync"/> whether it may consume the requested amount of a subject's quota BEFORE it does any work,
/// and records the consumption with <see cref="RecordConsumptionAsync"/> only AFTER the work succeeds (a command that
/// releases a counted resource — a session ending — calls <see cref="ReleaseAsync"/>). The decision is computed
/// entirely server-side from the catalog limit and the recorded usage; the client supplies no part of it, so a
/// client can never raise its own limit (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Never trust
/// client-side premium flags").
///
/// FAIL-CLOSED AND CONSISTENT WITH THE STATUS READ. The enforcement decision reuses the SINGLE quota math
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
/// authorize the subject before calling the calculator. It is not transactional with the command's own write (the
/// repositories save independently), so the check runs before the work and the usage is recorded after it succeeds;
/// a concurrent over-limit race is bounded by re-checking on every command (defence in depth) and is noted as a
/// follow-up for a row-level reservation.
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
    /// Decides whether the subject may consume <paramref name="amount"/> more of the quota named by
    /// <paramref name="entitlementKey"/>, WITHOUT changing any state. The command calls this before doing its work
    /// and proceeds only when <see cref="QuotaEnforcementDecision.IsAllowed"/> is <see langword="true"/>. The
    /// decision is fail-closed: a subject not entitled to a defined quota has zero allowance; an unlimited grant has
    /// headroom; an ungoverned command (no active definition) is allowed.
    /// </summary>
    /// <param name="subjectType">The kind of subject the quota is measured for.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="entitlementKey">The generic quota entitlement key the command consumes (see <see cref="QuotaEntitlementKeys"/>).</param>
    /// <param name="amount">The amount the command would consume (strictly positive).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
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
            // No active quota governs this command in this deployment: there is nothing to enforce. Core enforces
            // only quotas that exist (a deployment that wants a free limit defines the quota and grants the
            // entitlement), so the command proceeds and nothing is recorded.
            return QuotaEnforcementDecision.NotGoverned(entitlementKey);
        }

        var status = await CalculateStatusAsync(subjectType, subjectId, definition, cancellationToken)
            .ConfigureAwait(false);

        // Allowed iff the requested amount fits the remaining headroom. An unlimited grant has null remaining
        // (treated as infinite); a not-entitled subject has zero remaining (fail-closed: no allowance); a capped
        // grant has max(0, limit - used).
        var allowed = (status.Remaining ?? long.MaxValue) >= amount;
        return allowed
            ? QuotaEnforcementDecision.Allowed(status)
            : QuotaEnforcementDecision.Denied(status);
    }

    /// <summary>
    /// Records that the subject has consumed <paramref name="amount"/> more of the quota named by
    /// <paramref name="entitlementKey"/>, by incrementing its recorded usage (creating the usage row at the amount
    /// on first consumption). Called by a protected command only AFTER its work succeeds, so a failed command never
    /// consumes quota. When no active quota governs the command, nothing is recorded.
    /// </summary>
    /// <exception cref="ArgumentException">The subject id is empty or the key is malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The amount is not strictly positive.</exception>
    public async Task RecordConsumptionAsync(
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
            // Ungoverned command: there is no quota definition to key a usage row to, so nothing is tracked.
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var existing = await _quotaUsage
            .FindBySubjectAndQuotaAsync(subjectType, subjectId, definition.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.Record(existing.UsedAmount + amount, now);
            await _quotaUsage.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
            return;
        }

        var usage = QuotaUsage.Start(subjectType, subjectId, definition, now);
        usage.Record(amount, now);
        var addResult = await _quotaUsage.AddAsync(usage, cancellationToken).ConfigureAwait(false);
        if (addResult == QuotaUsageAddResult.DuplicateUsage)
        {
            // A concurrent command inserted the usage row first (lost create race). Re-read the now-existing row and
            // increment it, so the consumption is never silently dropped.
            var reloaded = await _quotaUsage
                .FindBySubjectAndQuotaAsync(subjectType, subjectId, definition.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Quota usage insert reported a duplicate but the existing row could not be reloaded.");
            reloaded.Record(reloaded.UsedAmount + amount, now);
            await _quotaUsage.UpdateAsync(reloaded, cancellationToken).ConfigureAwait(false);
        }
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
    /// Computes the subject's current <see cref="QuotaStatus"/> for one quota, reusing the same inputs and math the
    /// quota-status read uses: the subject's granted limit (resolved from its ACTIVE entitlements, so a revoked
    /// grant immediately removes the headroom) and its recorded usage (absent ⇒ zero). Sharing
    /// <see cref="QuotaStatus.Calculate"/> guarantees enforcement and the status read can never diverge.
    /// </summary>
    private async Task<QuotaStatus> CalculateStatusAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        QuotaDefinition definition,
        CancellationToken cancellationToken)
    {
        var entitlements = await _entitlementResolver
            .ResolveAsync(subjectType, subjectId, cancellationToken)
            .ConfigureAwait(false);
        var isEntitled = entitlements.TryGetQuotaLimit(definition.EntitlementKey, out var limit);

        var existing = await _quotaUsage
            .FindBySubjectAndQuotaAsync(subjectType, subjectId, definition.Id, cancellationToken)
            .ConfigureAwait(false);
        var used = existing?.UsedAmount ?? 0L;

        return QuotaStatus.Calculate(definition.EntitlementKey, definition.Unit, isEntitled, limit, used);
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
