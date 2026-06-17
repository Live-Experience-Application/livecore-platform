// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of granting a subject the entitlements mapped to a purchased product
/// (CORE-MON-003, <see cref="ProductEntitlementGrantService.GrantForProductAsync"/>).
/// </summary>
public enum ProductEntitlementGrantOutcome
{
    /// <summary>
    /// The product reference mapped to an active plan and its entitlements were assigned to the subject (see the
    /// inner <see cref="ProductEntitlementGrantResult.Assignment"/> for what changed).
    /// </summary>
    Granted = 1,

    /// <summary>
    /// No active plan is mapped to the product reference (an unknown product, a retired plan, or a reference that
    /// is not a valid plan-key shape); nothing was granted (fail-closed). A verified purchase for an ungoverned
    /// product never unlocks premium state.
    /// </summary>
    ProductNotMapped = 2,
}

/// <summary>
/// Result of the purchase-to-entitlement grant chain (CORE-MON-003). Beyond the <see cref="Outcome"/> it carries
/// the resolved <see cref="PlanDefinitionId"/> and the underlying CORE-ENTL-002
/// <see cref="SubjectEntitlementAssignmentResult"/> (what the assignment changed) when a grant happened, so a
/// caller can act on or log the grant without a second lookup.
/// </summary>
/// <param name="Outcome">Whether the product mapped to a plan and was granted, or mapped to nothing.</param>
/// <param name="PlanDefinitionId">The plan the product mapped to (set only when <see cref="Outcome"/> is granted).</param>
/// <param name="Assignment">The assignment result (set only when <see cref="Outcome"/> is granted).</param>
public sealed record ProductEntitlementGrantResult(
    ProductEntitlementGrantOutcome Outcome,
    Guid? PlanDefinitionId,
    SubjectEntitlementAssignmentResult? Assignment)
{
    /// <summary>A result for a product reference that maps to no active plan; nothing was granted (fail-closed).</summary>
    public static ProductEntitlementGrantResult ProductNotMapped { get; } =
        new(ProductEntitlementGrantOutcome.ProductNotMapped, null, null);

    /// <summary>A result for a product reference that mapped to an active plan and was granted.</summary>
    public static ProductEntitlementGrantResult Granted(Guid planDefinitionId, SubjectEntitlementAssignmentResult assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return new ProductEntitlementGrantResult(ProductEntitlementGrantOutcome.Granted, planDefinitionId, assignment);
    }
}
