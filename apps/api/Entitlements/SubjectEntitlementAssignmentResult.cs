// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of assigning a plan's entitlements to a subject (CORE-ENTL-002,
/// <see cref="SubjectEntitlementAssignmentService.AssignFromPlanAsync"/>).
/// </summary>
public enum SubjectEntitlementAssignmentOutcome
{
    /// <summary>The plan was resolved and its grants were assigned to the subject (see the counts for detail).</summary>
    Assigned = 1,

    /// <summary>No plan exists for the given id; nothing was assigned (fail-closed).</summary>
    PlanNotFound = 2,

    /// <summary>The plan is retired (inactive) and is no longer offered; nothing was assigned (fail-closed).</summary>
    PlanInactive = 3,
}

/// <summary>
/// Result of assigning a plan's entitlements to a subject (CORE-ENTL-002). Beyond the
/// <see cref="Outcome"/> it reports how the subject's assignments changed: how many were newly created, how many
/// existing assignments were updated to the plan's values (an upgrade or re-grant), and how many grants were
/// skipped because their entitlement definition is retired and the subject did not already hold it (a retired
/// entitlement is never newly assigned — fail-closed).
/// </summary>
/// <param name="Outcome">Whether the plan was assigned, not found, or inactive.</param>
/// <param name="AssignedCount">Grants that created a new subject entitlement.</param>
/// <param name="UpdatedCount">Grants that updated an existing subject entitlement (value, source plan, reinstatement).</param>
/// <param name="SkippedCount">Grants skipped because their definition is retired and the subject did not already hold it.</param>
public sealed record SubjectEntitlementAssignmentResult(
    SubjectEntitlementAssignmentOutcome Outcome,
    int AssignedCount,
    int UpdatedCount,
    int SkippedCount)
{
    /// <summary>A result for a plan that does not exist; nothing changed.</summary>
    public static SubjectEntitlementAssignmentResult PlanNotFound { get; } =
        new(SubjectEntitlementAssignmentOutcome.PlanNotFound, 0, 0, 0);

    /// <summary>A result for a retired plan; nothing changed.</summary>
    public static SubjectEntitlementAssignmentResult PlanInactive { get; } =
        new(SubjectEntitlementAssignmentOutcome.PlanInactive, 0, 0, 0);
}
