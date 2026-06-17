// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of persisting a new plan definition (CORE-ENTL-001).
///
/// The plan <see cref="PlanDefinition.Key"/> is a globally unique natural key, so an insert can race or repeat
/// another plan's key; this enum mirrors <c>OrganizationAddResult</c>: a success value and an explicit
/// duplicate-key outcome. Any other failure (for example a foreign-key violation for a granted entitlement
/// definition that does not exist) surfaces as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as a result
/// value.
/// </summary>
public enum PlanDefinitionAddResult
{
    /// <summary>The plan definition (with its grants) was persisted.</summary>
    Added = 1,

    /// <summary>A plan definition with the same key already exists; the insert was rejected.</summary>
    DuplicateKey = 2,
}
