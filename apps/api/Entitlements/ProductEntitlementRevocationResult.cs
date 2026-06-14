namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of revoking a subject's entitlements mapped to a purchased product
/// (CORE-MON-004, <see cref="ProductEntitlementGrantService.RevokeForProductAsync"/>) — the inverse of
/// <see cref="ProductEntitlementGrantOutcome"/>.
/// </summary>
public enum ProductEntitlementRevocationOutcome
{
    /// <summary>
    /// The product reference mapped to a plan and the plan's entitlements were revoked from the subject (see the
    /// inner <see cref="ProductEntitlementRevocationResult.RevokedCount"/> for how many assignments were revoked).
    /// </summary>
    Revoked = 1,

    /// <summary>
    /// No plan is mapped to the product reference (an unknown product or a reference that is not a valid plan-key
    /// shape); nothing was revoked. A refund/cancellation of an ungoverned product has no granted entitlement to
    /// revoke.
    /// </summary>
    ProductNotMapped = 2,
}

/// <summary>
/// Result of revoking the entitlements a purchased product maps to (CORE-MON-004). Beyond the
/// <see cref="Outcome"/> it carries the resolved <see cref="PlanDefinitionId"/> and how many of the subject's
/// assignments were actually revoked (an assignment the subject does not hold contributes nothing), so a caller can
/// act on or log the revocation without a second lookup. The revocation is the inverse of the CORE-MON-003 grant
/// chain and reuses the same product → plan mapping.
/// </summary>
/// <param name="Outcome">Whether the product mapped to a plan and was revoked, or mapped to nothing.</param>
/// <param name="PlanDefinitionId">The plan the product mapped to (set only when <see cref="Outcome"/> is revoked).</param>
/// <param name="RevokedCount">How many subject entitlements were revoked (assignments the subject held; 0 otherwise).</param>
public sealed record ProductEntitlementRevocationResult(
    ProductEntitlementRevocationOutcome Outcome,
    Guid? PlanDefinitionId,
    int RevokedCount)
{
    /// <summary>A result for a product reference that maps to no plan; nothing was revoked.</summary>
    public static ProductEntitlementRevocationResult ProductNotMapped { get; } =
        new(ProductEntitlementRevocationOutcome.ProductNotMapped, null, 0);

    /// <summary>A result for a product reference that mapped to a plan and whose entitlements were revoked.</summary>
    public static ProductEntitlementRevocationResult Revoked(Guid planDefinitionId, int revokedCount)
        => new(ProductEntitlementRevocationOutcome.Revoked, planDefinitionId, revokedCount);
}
