// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Persistence contract for the entitlement definition aggregate (CORE-ENTL-001). The Entitlements module owns
/// the <c>entitlement_definitions</c> table; other modules access entitlement definitions only through this
/// contract or the module's application services (docs/02_ARCHITECTURE.md: no direct table ownership
/// violations).
///
/// Unlike the tenant-scoped aggregates (recap, export job, ...), the entitlement definition is a GLOBAL
/// monetization catalog (csv/database_tables.csv scope <c>global</c>, like <c>organizations</c>/<c>users</c>/
/// <c>templates</c>), so there is no <c>organization_id</c> to scope lookups by and a list-everything read is
/// legitimate (the catalog is the same for every tenant; threat T5 tenant isolation does not apply to a global
/// definition table). Lookups are by the surrogate id or the stable, unique natural <see cref="EntitlementDefinition.Key"/>.
/// </summary>
public interface IEntitlementDefinitionRepository
{
    /// <summary>
    /// Persists a new entitlement definition. The key is a globally unique natural key, so a duplicate key is
    /// reported as <see cref="EntitlementDefinitionAddResult.DuplicateKey"/>; any other failure surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<EntitlementDefinitionAddResult> AddAsync(
        EntitlementDefinition definition,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the entitlement definition with exactly the given id, or <see langword="null"/> when none exists.
    /// </summary>
    /// <exception cref="System.ArgumentException">The id is empty (an empty id can never address a stored row).</exception>
    Task<EntitlementDefinition?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the entitlement definition addressed by exactly the given canonical key, or <see langword="null"/>
    /// when none exists. The key is canonicalized first, so a malformed value never addresses a definition.
    /// </summary>
    /// <exception cref="System.ArgumentException">The key violates the key invariants.</exception>
    Task<EntitlementDefinition?> FindByKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every entitlement definition in the catalog in a deterministic order (by the stable key). The
    /// catalog is global, so this is the catalog read, not a tenant-crossing leak.
    /// </summary>
    Task<IReadOnlyList<EntitlementDefinition>> ListAllAsync(CancellationToken cancellationToken);
}
