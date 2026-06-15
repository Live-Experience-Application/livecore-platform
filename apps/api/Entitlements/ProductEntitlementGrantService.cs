using LiveCore.Api.Audit;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// The purchase-to-entitlement GRANT CHAIN (CORE-MON-003, the grant-chain story of the "Monetization v1" epic).
/// It is the missing link the store stories pointed to: a verified, buyer-linked purchase grants the buyer the
/// corresponding <see cref="SubjectEntitlement"/> through the documented product → plan → entitlement mapping
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification" step 5 "Backend grants
/// SubjectEntitlement"; docs/24_SPEC_CONSISTENCY.md v1 monetization acceptance).
///
/// IT ONLY WIRES, IT DOES NOT DUPLICATE. The plan → entitlement bundle already exists
/// (<see cref="PlanDefinition.Entitlements"/>, CORE-ENTL-001) and the server-side assignment already exists
/// (<see cref="SubjectEntitlementAssignmentService.AssignFromPlanAsync"/>, CORE-ENTL-002); this service supplies
/// the remaining product → plan step and reuses both. The verified purchase's <c>product_reference</c> (the
/// vertical's opaque store product identifier) is mapped to a generic plan by the plan's stable
/// <see cref="PlanDefinition.Key"/> — Core provides only the generic mechanism, and the vertical supplies the
/// plan-definition seed data whose keys correspond to the store products it sells (docs/21 / PlanDefinition: the
/// concrete commercial plans are vertical seed data, never hardcoded in Core). No new table is introduced: the
/// chain composes the existing <c>plan_definitions</c>/<c>plan_entitlements</c>/<c>subject_entitlements</c> model.
///
/// IDEMPOTENT ON (PURCHASE, ENTITLEMENT). A verified purchase maps to exactly one subject (the unique
/// <c>billing_account_links(purchase_transaction_id)</c> link, CORE-MON-002) and deterministically to one plan
/// (by product reference), and the assignment is idempotent per (subject, entitlement) (the unique per-subject
/// index, upsert-in-place — CORE-ENTL-002). So granting the same purchase's entitlements again — a client retry,
/// a replayed-but-genuine proof, a duplicate store notification — converges rather than double-granting, and the
/// grant shows up in the effective-entitlements read (<see cref="SubjectEntitlementResolver"/>).
///
/// FAIL-CLOSED. A product reference that maps to no active plan (an unknown product, a retired plan, or a value
/// that is not a valid plan-key shape) grants NOTHING (<see cref="ProductEntitlementGrantOutcome.ProductNotMapped"/>)
/// — premium state is never unlocked for an ungoverned product. Verification and the buyer linkage are upstream:
/// this service is invoked only after a purchase is verified server-side and linked to THIS subject, so an
/// unverified/failed purchase never reaches it ("Never unlock limits before server verification succeeds"; "Never
/// trust client-side premium flags", docs/21).
///
/// REVOKE IS THE INVERSE (CORE-MON-004). <see cref="RevokeForProductAsync(EntitlementSubjectType, Guid, string,
/// DateTimeOffset, CancellationToken)"/> revokes the same product → plan → entitlement mapping when a purchase is
/// refunded, cancelled or charged back ("Refunds and chargebacks must revoke or downgrade entitlements", docs/21):
/// it resolves the plan by the product reference and revokes each of the plan's entitlements from the subject
/// (reusing <see cref="SubjectEntitlementAssignmentService.RevokeAsync"/>), so the grant and its revocation share
/// one mapping and can never disagree. It is idempotent (revoking an already-revoked or never-held entitlement is a
/// safe no-op) and resolves the plan even when it is RETIRED — a refund must still revoke a previously-granted
/// entitlement of a now-retired plan.
///
/// NARROW CROSS-PRODUCT REVOCATION (CORE-MON-012). One subject can hold two purchases whose plans share an
/// entitlement (for example two products that both grant <c>ads.disabled</c>). Because a subject holds each
/// entitlement at most once, revoking ALL of a refunded product's entitlements would strip a shared entitlement the
/// subject's OTHER, still-active purchase legitimately grants. The retention overload
/// <see cref="RevokeForProductAsync(EntitlementSubjectType, Guid, string, DateTimeOffset, IReadOnlyCollection{string},
/// CancellationToken)"/> takes the product references of the subject's other still-active purchases, resolves the
/// union of entitlements those products still grant, and revokes only the refunded product's entitlements that are
/// NOT in that retained union — so an entitlement shared by a second still-active product is retained, and refunding
/// the last product holding it finally revokes it.
/// </summary>
public sealed class ProductEntitlementGrantService
{
    private readonly IPlanDefinitionRepository _plans;
    private readonly SubjectEntitlementAssignmentService _assignment;
    private readonly IAuditLogRepository? _auditLog;

    /// <param name="plans">The plan-definition repository the product → plan mapping resolves through.</param>
    /// <param name="assignment">The reused per-subject entitlement assignment/revoke primitive.</param>
    /// <param name="auditLog">
    /// The append-only audit log the grant/revoke records a real audit fact on (CORE-SPEC-002:
    /// <see cref="AuditAction.EntitlementGranted"/> / <see cref="AuditAction.EntitlementRevoked"/>, the catalog's
    /// EntitlementGranted/Revoked events). OPTIONAL: the API and worker hosts inject it so a real grant/revoke is
    /// audited as a PLATFORM-level fact (a purchase grant is deployment-spanning, not tenant-scoped — docs/21);
    /// it is null in focused unit tests of the mapping itself, where no audit is asserted. The grant/revoke is
    /// emitted in the SAME call as the assignment, so when the caller wraps it in a transaction (the purchase
    /// verification / store-notification unit of work, CORE-CONC-002) the audit commits or rolls back with it.
    /// </param>
    public ProductEntitlementGrantService(
        IPlanDefinitionRepository plans,
        SubjectEntitlementAssignmentService assignment,
        IAuditLogRepository? auditLog = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(assignment);
        _plans = plans;
        _assignment = assignment;
        _auditLog = auditLog;
    }

    /// <summary>
    /// Grants the subject the entitlements of the plan mapped to <paramref name="productReference"/>. Resolves the
    /// active plan whose <see cref="PlanDefinition.Key"/> the product reference names and assigns its grants to the
    /// subject (reusing <see cref="SubjectEntitlementAssignmentService.AssignFromPlanAsync"/>). Returns
    /// <see cref="ProductEntitlementGrantResult.ProductNotMapped"/> — granting nothing — when the product reference
    /// maps to no active plan (fail-closed). Idempotent: granting the same product to the same subject again
    /// converges in place.
    /// </summary>
    /// <param name="subjectType">The kind of subject to grant to (the buyer is a <see cref="EntitlementSubjectType.User"/>).</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="productReference">The verified purchase's opaque store product reference.</param>
    /// <param name="grantedAt">When the grant happens.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">The subject type is not a defined value.</exception>
    /// <exception cref="ArgumentException">The subject id is empty.</exception>
    public async Task<ProductEntitlementGrantResult> GrantForProductAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        string productReference,
        DateTimeOffset grantedAt,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(subjectType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(subjectType),
                subjectType,
                "Subject type is not a defined entitlement subject type.");
        }

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        // Map the product reference to a generic plan by its key. A blank reference, or one that is not a valid
        // plan-key shape, can name no plan — so it maps to nothing and grants nothing (fail-closed) rather than
        // throwing. The reference is canonicalized to the stored key form (trimmed + lower-cased) before lookup.
        if (string.IsNullOrWhiteSpace(productReference))
        {
            return ProductEntitlementGrantResult.ProductNotMapped;
        }

        var planKey = productReference.Trim().ToLowerInvariant();
        if (!PlanDefinition.IsValidKey(planKey))
        {
            return ProductEntitlementGrantResult.ProductNotMapped;
        }

        var plan = await _plans.FindByKeyAsync(planKey, cancellationToken).ConfigureAwait(false);

        // Fail-closed: an unmapped product (no plan) or a retired plan grants nothing — premium state is never
        // unlocked without a real, active plan behind the purchase. (AssignFromPlanAsync would also reject an
        // inactive plan, but resolving by key first keeps "no mapping" and "retired plan" one fail-closed result.)
        if (plan is null || !plan.IsActive)
        {
            return ProductEntitlementGrantResult.ProductNotMapped;
        }

        // Assign the plan's entitlements to the buyer subject (REUSING the CORE-ENTL-002 assignment service). The
        // assignment is idempotent per (subject, entitlement), so a duplicate verified purchase / retry converges
        // in place rather than double-granting.
        var assignment = await _assignment
            .AssignFromPlanAsync(subjectType, subjectId, plan.Id, grantedAt, cancellationToken)
            .ConfigureAwait(false);

        // Record the grant as a real audit fact (CORE-SPEC-002: AuditAction.EntitlementGranted). A PLATFORM-level
        // fact — a purchase grant is deployment-spanning, not tenant-scoped (docs/21) — so it carries a null
        // organization and no actor (system-initiated by a verified purchase); the granted subject is the resource.
        if (_auditLog is not null)
        {
            await _auditLog
                .AppendAsync(
                    AuditLogEntry.ForEntitlementGranted(subjectType.ToString(), subjectId, grantedAt),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return ProductEntitlementGrantResult.Granted(plan.Id, assignment);
    }

    /// <summary>
    /// Revokes from the subject every entitlement of the plan mapped to <paramref name="productReference"/> — the
    /// inverse of <see cref="GrantForProductAsync"/> for a refund, cancellation or chargeback (CORE-MON-004; docs/21
    /// "Refunds and chargebacks must revoke or downgrade entitlements"). This is the unconditional revoke; the
    /// retention overload narrows it across a subject's other still-active products (CORE-MON-012).
    /// </summary>
    /// <param name="subjectType">The kind of subject to revoke from (the buyer is a <see cref="EntitlementSubjectType.User"/>).</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="productReference">The refunded/cancelled purchase's opaque store product reference.</param>
    /// <param name="revokedAt">When the revocation happens.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">The subject type is not a defined value.</exception>
    /// <exception cref="ArgumentException">The subject id is empty.</exception>
    public Task<ProductEntitlementRevocationResult> RevokeForProductAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        string productReference,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
        => RevokeForProductAsync(
            subjectType,
            subjectId,
            productReference,
            revokedAt,
            stillActiveProductReferences: null,
            cancellationToken);

    /// <summary>
    /// Revokes from the subject the entitlements of the plan mapped to <paramref name="productReference"/>, EXCEPT
    /// those still granted by another of the subject's active purchases (CORE-MON-012, the narrow cross-product
    /// revocation story). Resolves the plan whose <see cref="PlanDefinition.Key"/> the product reference names (the
    /// same product → plan mapping the grant uses), resolves the union of entitlements that
    /// <paramref name="stillActiveProductReferences"/> still grant, and revokes each of the refunded product's
    /// grants from the subject (reusing <see cref="SubjectEntitlementAssignmentService.RevokeAsync"/>) UNLESS it is
    /// in that retained union — so an entitlement a second still-active product also grants is retained, and only the
    /// refund of the last product holding it actually revokes it. Returns
    /// <see cref="ProductEntitlementRevocationResult.ProductNotMapped"/> — revoking nothing — when the product
    /// reference maps to no plan (fail-closed). Idempotent: an entitlement the subject does not hold (or already
    /// revoked) is a safe no-op, so revoking the same product twice converges.
    /// </summary>
    /// <param name="subjectType">The kind of subject to revoke from (the buyer is a <see cref="EntitlementSubjectType.User"/>).</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="productReference">The refunded/cancelled purchase's opaque store product reference.</param>
    /// <param name="revokedAt">When the revocation happens.</param>
    /// <param name="stillActiveProductReferences">
    /// The product references of the SAME subject's other still-active (non-revoked) purchases. The entitlements
    /// those products still grant are retained (not revoked). Null or empty means no other active purchase — the
    /// refunded product's entitlements are all revoked (the unconditional CORE-MON-004 behavior). A reference that
    /// maps to no plan contributes nothing, so a stray/unmapped value is a safe no-op.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">The subject type is not a defined value.</exception>
    /// <exception cref="ArgumentException">The subject id is empty.</exception>
    public async Task<ProductEntitlementRevocationResult> RevokeForProductAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        string productReference,
        DateTimeOffset revokedAt,
        IReadOnlyCollection<string>? stillActiveProductReferences,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(subjectType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(subjectType),
                subjectType,
                "Subject type is not a defined entitlement subject type.");
        }

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        // Map the product reference to a plan by its key, exactly as the grant does. A blank or invalid reference
        // names no plan, so it revokes nothing (fail-closed) rather than throwing.
        if (string.IsNullOrWhiteSpace(productReference))
        {
            return ProductEntitlementRevocationResult.ProductNotMapped;
        }

        var planKey = productReference.Trim().ToLowerInvariant();
        if (!PlanDefinition.IsValidKey(planKey))
        {
            return ProductEntitlementRevocationResult.ProductNotMapped;
        }

        // Resolve the plan REGARDLESS of whether it is still active: a refund must revoke a previously-granted
        // entitlement of a plan that has since been retired. Only a product that maps to no plan at all revokes
        // nothing.
        var plan = await _plans.FindByKeyAsync(planKey, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return ProductEntitlementRevocationResult.ProductNotMapped;
        }

        // CORE-MON-012: the union of entitlements the subject's OTHER still-active purchases still grant. Before
        // revoking one of this product's entitlements, check whether another active purchase still grants it; only
        // revoke if none does, so a shared entitlement is retained while any active purchase backs it.
        var retained = await ResolveRetainedEntitlementIdsAsync(stillActiveProductReferences, cancellationToken)
            .ConfigureAwait(false);

        // Revoke each of the plan's entitlements from the subject (REUSING the CORE-ENTL-002 revoke primitive),
        // skipping any still granted by another active purchase. Each revoke is idempotent — an assignment the
        // subject does not hold, or already revoked, is a safe no-op — so a duplicate refund / retry converges
        // rather than erroring.
        var revoked = 0;
        foreach (var grant in plan.Entitlements)
        {
            if (retained.Contains(grant.EntitlementDefinitionId))
            {
                // Still granted by another active purchase of the same subject — retain it (CORE-MON-012).
                continue;
            }

            if (await _assignment
                .RevokeAsync(subjectType, subjectId, grant.EntitlementDefinitionId, revokedAt, cancellationToken)
                .ConfigureAwait(false))
            {
                revoked++;
            }
        }

        // Record the revocation as a real audit fact (CORE-SPEC-002: AuditAction.EntitlementRevoked) ONLY when a
        // grant was actually revoked — a refund re-delivery (or a reconciliation no-op) that finds the entitlement
        // already revoked records nothing, so the audit trail is not spammed by idempotent retries. Like the grant
        // fact it is PLATFORM-level (null organization, no actor) with the affected subject as the resource.
        if (revoked > 0 && _auditLog is not null)
        {
            await _auditLog
                .AppendAsync(
                    AuditLogEntry.ForEntitlementRevoked(subjectType.ToString(), subjectId, revokedAt),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return ProductEntitlementRevocationResult.Revoked(plan.Id, revoked);
    }

    /// <summary>
    /// Resolves the union of entitlement-definition ids that the given still-active product references still grant —
    /// the set of entitlements a refund must RETAIN because another active purchase of the same subject backs them
    /// (CORE-MON-012). Each reference is mapped to its plan by the same product → plan key mapping the revoke uses
    /// (resolving the plan even when RETIRED, since an active purchase of a now-retired plan still grants its
    /// entitlements); a blank, invalid or unmapped reference contributes nothing (fail-closed). Returns an empty set
    /// when there are no other active products.
    /// </summary>
    private async Task<HashSet<Guid>> ResolveRetainedEntitlementIdsAsync(
        IReadOnlyCollection<string>? stillActiveProductReferences,
        CancellationToken cancellationToken)
    {
        var retained = new HashSet<Guid>();
        if (stillActiveProductReferences is null || stillActiveProductReferences.Count == 0)
        {
            return retained;
        }

        foreach (var productReference in stillActiveProductReferences)
        {
            if (string.IsNullOrWhiteSpace(productReference))
            {
                continue;
            }

            var planKey = productReference.Trim().ToLowerInvariant();
            if (!PlanDefinition.IsValidKey(planKey))
            {
                continue;
            }

            var plan = await _plans.FindByKeyAsync(planKey, cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                continue;
            }

            foreach (var grant in plan.Entitlements)
            {
                retained.Add(grant.EntitlementDefinitionId);
            }
        }

        return retained;
    }
}
