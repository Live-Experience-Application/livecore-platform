namespace LiveCore.Api.Entitlements;

/// <summary>
/// Server-side ASSIGNMENT of entitlements to a subject (CORE-ENTL-002) — the write half of the subject
/// entitlement assignment and lookup story. It is the application service that grants a subject the
/// entitlements of a plan and revokes them again, REUSING the CORE-ENTL-001 plan and entitlement definition
/// catalog rather than re-deriving values: the value a subject is granted comes from the plan's grants
/// (decided server-side), so a client never supplies a premium value (docs/21 "Never trust client-side premium
/// flags"; "Backend grants SubjectEntitlement" after a verified purchase).
///
/// This service performs the assignment mechanism only; it does not authorize the caller or verify a purchase.
/// The purchase-verification flow that TRIGGERS an assignment, and the store-notification downgrade/refund flow
/// that triggers a revocation, are later billing stories — this story supplies the generic assignment and
/// revocation primitives they build on (the same way the audit/export/recap models supplied the reusable core
/// their later endpoints sit on).
///
/// ASSIGNMENT IS IDEMPOTENT. Assigning a plan grants every entitlement the plan defines; re-assigning the same
/// (or a changed) plan updates the subject's existing assignments in place to the plan's current values and
/// reinstates any that were revoked, so a re-run converges rather than duplicating (a subject holds each
/// entitlement at most once — the unique per-subject index). A plan grant whose entitlement definition has
/// since been retired is only applied to an assignment the subject already holds; a retired entitlement is
/// never newly assigned (fail-closed).
/// </summary>
public sealed class SubjectEntitlementAssignmentService
{
    private readonly ISubjectEntitlementRepository _subjectEntitlements;
    private readonly IPlanDefinitionRepository _plans;
    private readonly IEntitlementDefinitionRepository _definitions;

    public SubjectEntitlementAssignmentService(
        ISubjectEntitlementRepository subjectEntitlements,
        IPlanDefinitionRepository plans,
        IEntitlementDefinitionRepository definitions)
    {
        ArgumentNullException.ThrowIfNull(subjectEntitlements);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(definitions);
        _subjectEntitlements = subjectEntitlements;
        _plans = plans;
        _definitions = definitions;
    }

    /// <summary>
    /// Assigns every entitlement of the given plan to the subject, at the plan's granted values. A new
    /// entitlement is created; an entitlement the subject already holds is updated to the plan's value (and
    /// reinstated if it had been revoked), recording the plan as the source. Returns a
    /// <see cref="SubjectEntitlementAssignmentResult"/> describing what changed; an unknown or retired plan
    /// assigns nothing (fail-closed).
    /// </summary>
    /// <param name="subjectType">The kind of subject to assign to.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="planDefinitionId">The plan whose grants are assigned.</param>
    /// <param name="assignedAt">When the assignment happens.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The subject id or plan id is empty.</exception>
    public async Task<SubjectEntitlementAssignmentResult> AssignFromPlanAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid planDefinitionId,
        DateTimeOffset assignedAt,
        CancellationToken cancellationToken)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        if (planDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Plan definition id must not be empty.", nameof(planDefinitionId));
        }

        var plan = await _plans.FindByIdAsync(planDefinitionId, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return SubjectEntitlementAssignmentResult.PlanNotFound;
        }

        // A retired plan is no longer offered, so it is never assigned (fail-closed). A revoked subject is
        // downgraded by revoking its assignments, not by assigning a retired plan.
        if (!plan.IsActive)
        {
            return SubjectEntitlementAssignmentResult.PlanInactive;
        }

        // The entitlement catalog is global; one full read resolves every grant's definition (for the
        // denormalized key and the active check). The subject's existing assignments are indexed by the
        // entitlement they grant so the loop can upsert in place.
        var definitionsById = (await _definitions.ListAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(definition => definition.Id);
        var existingByEntitlementId = (await _subjectEntitlements
                .ListBySubjectAsync(subjectType, subjectId, cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(assignment => assignment.EntitlementDefinitionId);

        var assigned = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var grant in plan.Entitlements)
        {
            if (existingByEntitlementId.TryGetValue(grant.EntitlementDefinitionId, out var existing))
            {
                // Update the existing assignment in place to the plan's current value, record the source plan
                // and reinstate it if it was revoked. The value kind is immutable, so the apply matches the
                // existing assignment's kind.
                if (grant.IsFlag)
                {
                    existing.ApplyFlagGrant(grant.FlagValue!.Value, plan.Id, assignedAt);
                }
                else
                {
                    existing.ApplyQuotaGrant(grant.QuotaLimit, plan.Id, assignedAt);
                }

                await _subjectEntitlements.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                updated++;
                continue;
            }

            // A new assignment requires an active definition (the grant factory rejects a retired one). A grant
            // whose definition has since been retired and that the subject does not already hold is skipped: a
            // retired entitlement is never newly assigned.
            if (!definitionsById.TryGetValue(grant.EntitlementDefinitionId, out var definition) || !definition.IsActive)
            {
                skipped++;
                continue;
            }

            var newAssignment = grant.IsFlag
                ? SubjectEntitlement.GrantFlag(subjectType, subjectId, definition, grant.FlagValue!.Value, plan.Id, assignedAt)
                : SubjectEntitlement.GrantQuota(subjectType, subjectId, definition, grant.QuotaLimit, plan.Id, assignedAt);

            var addResult = await _subjectEntitlements.AddAsync(newAssignment, cancellationToken).ConfigureAwait(false);
            if (addResult == SubjectEntitlementAddResult.Added)
            {
                assigned++;
            }
            else
            {
                // A concurrent assignment created the row between the read and the insert; the convergent
                // re-run will update it. Count it as skipped rather than failing the whole assignment.
                skipped++;
            }
        }

        return new SubjectEntitlementAssignmentResult(
            SubjectEntitlementAssignmentOutcome.Assigned,
            assigned,
            updated,
            skipped);
    }

    /// <summary>
    /// Revokes a single entitlement from a subject (a refund, cancellation or downgrade): the assignment is
    /// retained as a record but resolves to NOT entitled, so the subject loses the premium state (docs/21
    /// "Refunds and chargebacks must revoke or downgrade entitlements"). Returns <see langword="true"/> when an
    /// assignment was revoked, <see langword="false"/> when the subject did not hold the entitlement (nothing
    /// to revoke). Revoking an already-revoked assignment is a no-op that still returns <see langword="true"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The subject id or entitlement definition id is empty.</exception>
    public async Task<bool> RevokeAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid entitlementDefinitionId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var existing = await _subjectEntitlements
            .FindBySubjectAndEntitlementAsync(subjectType, subjectId, entitlementDefinitionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        existing.Revoke(revokedAt);
        await _subjectEntitlements.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
