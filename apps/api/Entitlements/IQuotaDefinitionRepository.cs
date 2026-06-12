namespace LiveCore.Api.Entitlements;

/// <summary>
/// Persistence contract for the quota definition aggregate (CORE-ENTL-003). The Entitlements module owns the
/// <c>quota_definitions</c> table; other modules access quota definitions only through this contract or the
/// module's application services (docs/02_ARCHITECTURE.md: no direct table ownership violations).
///
/// Like <see cref="IEntitlementDefinitionRepository"/>, the quota definition is a GLOBAL monetization catalog
/// (csv/database_tables.csv scope <c>global</c>), so there is no <c>organization_id</c> to scope lookups by and a
/// list-everything read is the catalog read, not a tenant-crossing leak (threat T5 does not apply to a global
/// definition table). The hot-path read the quota-status calculation uses is
/// <see cref="ListActiveBySubjectTypeAsync"/>: the active quotas defined for one subject kind.
/// </summary>
public interface IQuotaDefinitionRepository
{
    /// <summary>
    /// Persists a new quota definition. A quota measures at most one entitlement, so a second quota for the same
    /// measured entitlement is reported as <see cref="QuotaDefinitionAddResult.DuplicateEntitlement"/>; any other
    /// failure (for example a measured entitlement-definition foreign key that does not exist) surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<QuotaDefinitionAddResult> AddAsync(QuotaDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the quota definition with exactly the given id, or <see langword="null"/> when none exists.
    /// </summary>
    /// <exception cref="System.ArgumentException">The id is empty (an empty id can never address a stored row).</exception>
    Task<QuotaDefinition?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the ACTIVE quota definitions measured for the given subject kind, in a deterministic order (by the
    /// measured entitlement key). Retired definitions are excluded — they are no longer reported in quota status.
    /// This is the read the quota-status calculation enumerates.
    /// </summary>
    Task<IReadOnlyList<QuotaDefinition>> ListActiveBySubjectTypeAsync(
        EntitlementSubjectType subjectType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every quota definition in the catalog (active and retired) in a deterministic order (by the measured
    /// entitlement key). The catalog is global, so this is the catalog read, not a tenant-crossing leak.
    /// </summary>
    Task<IReadOnlyList<QuotaDefinition>> ListAllAsync(CancellationToken cancellationToken);
}
