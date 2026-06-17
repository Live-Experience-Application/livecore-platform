// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of persisting a new subject entitlement assignment (CORE-ENTL-002).
///
/// A subject holds each entitlement AT MOST ONCE (the unique <c>subject_entitlements(subject_type, subject_id,
/// entitlement_definition_id)</c> index), so an insert can race or repeat an existing assignment of the same
/// entitlement to the same subject; this enum mirrors <see cref="EntitlementDefinitionAddResult"/>: a success
/// value and an explicit duplicate outcome. The assignment service updates an existing assignment in place
/// rather than inserting a second row, so this duplicate outcome is the fail-closed signal that the assignment
/// already exists. Any other failure surfaces as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as a result
/// value.
/// </summary>
public enum SubjectEntitlementAddResult
{
    /// <summary>The subject entitlement assignment was persisted.</summary>
    Added = 1,

    /// <summary>The subject is already assigned this entitlement; the insert was rejected.</summary>
    DuplicateAssignment = 2,
}
