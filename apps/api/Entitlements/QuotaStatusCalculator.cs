// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Server-side CALCULATION of a subject's quota status (CORE-ENTL-003) — the headline behavior of the quota
/// definition and quota status story. It combines, for a subject, the active <see cref="QuotaDefinition"/>s of the
/// subject's kind, the subject's granted limits (resolved through <see cref="SubjectEntitlementResolver"/>) and
/// the subject's recorded <see cref="QuotaUsage"/> into a <see cref="SubjectQuotaStatus"/> — the epic acceptance
/// criterion "Quota status is calculated server-side for subjects and workspaces".
///
/// The calculator is the single path that produces quota status, and it reads ONLY server-side state: the catalog
/// of quota definitions, the subject's active entitlements (the resolver excludes revoked grants — a refund /
/// cancellation / downgrade immediately removes the headroom) and the subject's recorded usage. There is no client
/// input and no other source, so a later <c>GET /api/v1/me/quota-status</c> response and an internal enforcement
/// check (CORE-ENTL-004) can never diverge.
///
/// The calculator is keyed purely by the (subject type, subject id) pair; it does not itself authorize the caller.
/// The endpoint that exposes a subject's quota status resolves and authorizes the subject first (the authenticated
/// user for <c>/me/quota-status</c>, or a tenant- and membership-checked workspace for the workspace surface) and
/// only then asks the calculator — exactly as the audit/export/recap projectors are the reusable core their
/// endpoints sit on.
/// </summary>
public sealed class QuotaStatusCalculator
{
    private readonly IQuotaDefinitionRepository _quotaDefinitions;
    private readonly SubjectEntitlementResolver _entitlementResolver;
    private readonly IQuotaUsageRepository _quotaUsage;

    public QuotaStatusCalculator(
        IQuotaDefinitionRepository quotaDefinitions,
        SubjectEntitlementResolver entitlementResolver,
        IQuotaUsageRepository quotaUsage)
    {
        ArgumentNullException.ThrowIfNull(quotaDefinitions);
        ArgumentNullException.ThrowIfNull(entitlementResolver);
        ArgumentNullException.ThrowIfNull(quotaUsage);
        _quotaDefinitions = quotaDefinitions;
        _entitlementResolver = entitlementResolver;
        _quotaUsage = quotaUsage;
    }

    /// <summary>
    /// Calculates the given subject's quota status across every active quota defined for its kind. Loads the
    /// active quota definitions for the subject kind, resolves the subject's effective (active) entitlements for
    /// the granted limits, and reads the subject's recorded usage; then combines them quota-by-quota. A subject
    /// with no entitlement for a defined quota is reported fail-closed (not entitled, no allowance), and a quota
    /// with no usage row reads as zero used.
    /// </summary>
    /// <param name="subjectType">The kind of subject to calculate for.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The subject id is empty.</exception>
    public async Task<SubjectQuotaStatus> CalculateForSubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        var definitions = await _quotaDefinitions
            .ListActiveBySubjectTypeAsync(subjectType, cancellationToken)
            .ConfigureAwait(false);
        var entitlements = await _entitlementResolver
            .ResolveAsync(subjectType, subjectId, cancellationToken)
            .ConfigureAwait(false);
        var usage = await _quotaUsage
            .ListBySubjectAsync(subjectType, subjectId, cancellationToken)
            .ConfigureAwait(false);

        return Combine(subjectType, subjectId, definitions, entitlements, usage);
    }

    /// <summary>
    /// Pure combination of a subject's quota definitions, effective entitlements and recorded usage into its
    /// quota status. Separated from the data loading above so the calculation is exhaustively unit-testable
    /// without a database (the "Quota calculation and boundary tests"). For each definition the granted limit
    /// comes from the matching active quota entitlement (absent ⇒ not entitled, fail-closed) and the used amount
    /// from the matching usage row (absent ⇒ zero); the per-quota math is <see cref="QuotaStatus.Calculate"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The subject id is empty, or two usage rows reference the same quota definition.</exception>
    public static SubjectQuotaStatus Combine(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        IReadOnlyList<QuotaDefinition> definitions,
        EffectiveEntitlements entitlements,
        IReadOnlyList<QuotaUsage> usage)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(entitlements);
        ArgumentNullException.ThrowIfNull(usage);

        // Index the recorded usage by the quota it measures. A subject records each quota at most once (the unique
        // per-subject index), so a duplicate is a data-invariant violation and is rejected rather than masking one
        // row's usage with another's.
        var usedByDefinition = new Dictionary<Guid, long>();
        foreach (var row in usage)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (!usedByDefinition.TryAdd(row.QuotaDefinitionId, row.UsedAmount))
            {
                throw new ArgumentException(
                    $"Duplicate quota usage for definition '{row.QuotaDefinitionId}'; a subject records each quota at most once.",
                    nameof(usage));
            }
        }

        var statuses = new List<QuotaStatus>(definitions.Count);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);

            // The granted limit is the subject's active quota entitlement for the measured key. TryGetQuotaLimit
            // distinguishes "no entitlement" (fail-closed: no allowance) from a granted limit (a number, or null
            // for an unlimited/fair-use grant).
            var isEntitled = entitlements.TryGetQuotaLimit(definition.EntitlementKey, out var limit);
            var used = usedByDefinition.GetValueOrDefault(definition.Id, 0L);

            statuses.Add(QuotaStatus.Calculate(definition.EntitlementKey, definition.Unit, isEntitled, limit, used));
        }

        return SubjectQuotaStatus.Create(subjectType, subjectId, statuses);
    }
}
