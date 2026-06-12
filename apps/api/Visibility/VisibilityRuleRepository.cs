using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Visibility;

/// <summary>
/// EF Core implementation of <see cref="IVisibilityRuleRepository"/> (CORE-VIS-001), backed by the
/// <c>visibility_rules</c> table mapped in <see cref="VisibilityRuleConfiguration"/>.
/// </summary>
internal sealed class VisibilityRuleRepository : IVisibilityRuleRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public VisibilityRuleRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<VisibilityRule?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored rule (ids are generated non-empty), so the lookup
        // fails fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Visibility rule id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant
        // column. The lookup is exactly tenant- and workspace-scoped, so a rule under another
        // organization or workspace is never returned even when the surrogate id matches (threat
        // T5/T1).
        return await _dbContext.VisibilityRules
            .FirstOrDefaultAsync(
                rule => rule.OrganizationId == organizationId
                    && rule.WorkspaceId == workspaceId
                    && rule.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VisibilityRule>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's rules, so the lookup fails fast instead
        // of returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column and then matches the workspace, so the list is
        // exactly tenant- and workspace-scoped: another tenant's or another workspace's rules are
        // never returned even when their ids would otherwise be addressable (threat T5/T1; the
        // organization boundary is checked before the workspace boundary). The ordering is
        // deterministic — sorted by the time-ordered surrogate id.
        return await _dbContext.VisibilityRules
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId)
            .OrderBy(rule => rule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VisibilityRule>> ListByResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored resource's rules, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource id must not be empty.", nameof(resourceId));
        }

        // The predicate leads with the tenant column, then matches the workspace, the resource type
        // and the resource id — the documented critical index shape
        // visibility_rules(workspace_id, resource_type, resource_id). So the list is exactly tenant-,
        // workspace- and resource-scoped: another tenant's or workspace's rules are never returned
        // even when their ids would otherwise be addressable (threat T5/T1). The ordering is
        // deterministic — sorted by the time-ordered surrogate id.
        return await _dbContext.VisibilityRules
            .Where(rule => rule.OrganizationId == organizationId
                && rule.WorkspaceId == workspaceId
                && rule.ResourceType == resourceType
                && rule.ResourceId == resourceId)
            .OrderBy(rule => rule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VisibilityRuleAddResult> AddAsync(VisibilityRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);

        _dbContext.VisibilityRules.Add(rule);

        // The critical index is non-unique, so there is no duplicate outcome to translate here; a
        // foreign-key violation (a non-existent workspace or tenant) propagates as a
        // DbUpdateException.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return VisibilityRuleAddResult.Added;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(VisibilityRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // The rule was loaded and mutated within this scope's change tracker (or is attached here);
        // only the mutable visibility state and update timestamp change. The organization,
        // workspace, id and governed resource are immutable on the aggregate, so an update can never
        // move the row to another tenant, workspace or resource (threat T5).
        _dbContext.VisibilityRules.Update(rule);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
