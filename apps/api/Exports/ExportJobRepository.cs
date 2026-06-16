using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Exports;

/// <summary>
/// EF Core implementation of <see cref="IExportJobRepository"/> (CORE-AUD-002), backed by the
/// <c>export_jobs</c> table mapped in <see cref="ExportJobConfiguration"/>.
/// </summary>
internal sealed class ExportJobRepository : IExportJobRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public ExportJobRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<ExportJob?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored job (ids are generated non-empty), so the lookup fails fast
        // instead of returning an arbitrary row.
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
            throw new ArgumentException("Export job id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant column.
        // The lookup is exactly tenant- and workspace-scoped, so a job under another organization or
        // workspace is never returned even when the surrogate id matches (threat T5/T1).
        return await _dbContext.ExportJobs
            .FirstOrDefaultAsync(
                job => job.OrganizationId == organizationId
                    && job.WorkspaceId == workspaceId
                    && job.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExportJob?> FindByIdInOrganizationAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored job (ids are generated non-empty), so the lookup fails fast
        // instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Export job id must not be empty.", nameof(id));
        }

        // The predicate leads with the tenant column, so the lookup is exactly tenant-scoped: a job under
        // another organization is never returned even when the surrogate id matches (threat T5/T1). The
        // caller authorizes against the loaded job's OWN workspace afterwards (load-then-authorize), exactly
        // as the asset signed-download route does.
        return await _dbContext.ExportJobs
            .FirstOrDefaultAsync(
                job => job.OrganizationId == organizationId && job.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExportJob>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's jobs, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column and then matches the workspace, so the list is
        // exactly tenant- and workspace-scoped: another tenant's or another workspace's jobs are never
        // returned even when their ids would otherwise be addressable (threat T5/T1; the organization
        // boundary is checked before the workspace boundary). The ordering is deterministic — sorted by
        // the surrogate id, which is time-ordered (UUIDv7), so the sequence is stable and repeatable.
        return await _dbContext.ExportJobs
            .Where(job => job.OrganizationId == organizationId
                && job.WorkspaceId == workspaceId)
            .OrderBy(job => job.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExportJobAddResult> AddAsync(ExportJob exportJob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exportJob);

        _dbContext.ExportJobs.Add(exportJob);

        // An export job has no uniqueness constraint to violate in this story, so there is no duplicate
        // outcome to translate here; a foreign-key violation (a non-existent workspace, tenant or
        // requesting user) propagates as a DbUpdateException.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ExportJobAddResult.Added;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(ExportJob exportJob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exportJob);

        // The job was loaded and mutated within this scope's change tracker (or is attached here); only
        // the mutable status, failure reason and update timestamp change. The organization, workspace, id,
        // requester and scope are immutable on the aggregate, so an update can never move the row to
        // another tenant or workspace, nor widen its scope (threats T5/T8).
        _dbContext.ExportJobs.Update(exportJob);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
