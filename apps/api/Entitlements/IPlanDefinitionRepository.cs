// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Persistence contract for the plan definition aggregate (CORE-ENTL-001). The Entitlements module owns the
/// <c>plan_definitions</c> table (and its <c>plan_entitlements</c> child rows); other modules access plan
/// definitions only through this contract or the module's application services.
///
/// Like <see cref="IEntitlementDefinitionRepository"/>, the plan definition is a GLOBAL monetization catalog
/// (csv/database_tables.csv scope <c>global</c>), so there is no <c>organization_id</c> to scope lookups by and
/// a list-everything read is legitimate. A plan is loaded WITH its grants (the
/// <see cref="PlanDefinition.Entitlements"/> child collection). Lookups are by the surrogate id or the stable,
/// unique natural <see cref="PlanDefinition.Key"/>.
/// </summary>
public interface IPlanDefinitionRepository
{
    /// <summary>
    /// Persists a new plan definition together with its grants (one graph, one transaction). The key is a
    /// globally unique natural key, so a duplicate key is reported as
    /// <see cref="PlanDefinitionAddResult.DuplicateKey"/>; any other failure (for example a granted
    /// entitlement-definition foreign key that does not exist) surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<PlanDefinitionAddResult> AddAsync(PlanDefinition plan, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the plan definition with exactly the given id (with its grants loaded), or <see langword="null"/>
    /// when none exists.
    /// </summary>
    /// <exception cref="System.ArgumentException">The id is empty.</exception>
    Task<PlanDefinition?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the plan definition addressed by exactly the given canonical key (with its grants loaded), or
    /// <see langword="null"/> when none exists. The key is canonicalized first.
    /// </summary>
    /// <exception cref="System.ArgumentException">The key violates the key invariants.</exception>
    Task<PlanDefinition?> FindByKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every plan definition in the catalog (with its grants loaded) in a deterministic order (by the
    /// stable key). The catalog is global, so this is the catalog read.
    /// </summary>
    Task<IReadOnlyList<PlanDefinition>> ListAllAsync(CancellationToken cancellationToken);
}
