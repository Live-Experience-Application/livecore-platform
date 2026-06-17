// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of persisting a new entitlement definition (CORE-ENTL-001).
///
/// The entitlement <see cref="EntitlementDefinition.Key"/> is a globally unique natural key, so an insert can
/// race or repeat another definition's key; this enum mirrors <c>OrganizationAddResult</c>: a success value
/// and an explicit duplicate-key outcome. Any other failure surfaces as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as a result
/// value.
/// </summary>
public enum EntitlementDefinitionAddResult
{
    /// <summary>The entitlement definition was persisted.</summary>
    Added = 1,

    /// <summary>An entitlement definition with the same key already exists; the insert was rejected.</summary>
    DuplicateKey = 2,
}
