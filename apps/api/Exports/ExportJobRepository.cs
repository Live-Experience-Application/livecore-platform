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

    /// <inheritdoc />
    public async Task<ExportJob?> ReloadAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored job (ids are generated non-empty), so the lookup fails fast.
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

        // Detach any copy of this job already tracked by this unit of work. After a rolled-back processing
        // attempt the tracked instance still carries the rolled-back transitions (it is Running/Completed in
        // memory while the committed row is still Pending); leaving it tracked would both return that stale
        // state below (EF returns the identity-map instance) and risk flushing the rolled-back transitions in a
        // later SaveChanges. Detaching it clears the identity map so the query materializes the durable row and
        // a subsequent attempt-counter update commits cleanly (CORE-RES-002).
        var tracked = _dbContext.ChangeTracker
            .Entries<ExportJob>()
            .FirstOrDefault(entry => entry.Entity.Id == id);
        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        // Re-read the authoritative row, tenant- and workspace-scoped exactly like FindByIdAsync (the
        // organization boundary leads, so another tenant's or workspace's job is never returned; threat T5/T1).
        return await _dbContext.ExportJobs
            .FirstOrDefaultAsync(
                job => job.OrganizationId == organizationId
                    && job.WorkspaceId == workspaceId
                    && job.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExportJob>> ListCompletedForRetentionAsync(
        DateTimeOffset createdBefore,
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The batch size must be positive.");
        }

        // SYSTEM retention sweep (CORE-PRIV-003): the predicate filters on STATUS, so a pending, running or failed
        // job is never returned even if it is old — only completed jobs (their produced artifact is downloadable
        // and may carry an object-storage blob) are purge candidates. It deliberately spans all tenants/workspaces
        // because the sweep is a system job, not a tenant actor.
        //
        // The order is the time-ordered surrogate id (UUIDv7 derived from the creation time, chronological and
        // provider-independent — SQLite cannot ORDER BY or compare a DateTimeOffset), oldest first, so the OLDEST
        // completed jobs sort first. The bounded batch (Take(maxCount)) therefore surfaces the most-aged jobs, and
        // the creation-age threshold is applied AFTER materialization so a still-recent completed job that slips in
        // is filtered out. Because id order tracks creation order, every past-window completed job is covered over
        // repeated sweeps without starvation.
        var oldestCompleted = await _dbContext.ExportJobs
            .AsNoTracking()
            .Where(job => job.Status == ExportJobStatus.Completed)
            .OrderBy(job => job.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return oldestCompleted
            .Where(job => job.CreatedAt < createdBefore)
            .ToList();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ExportJob exportJob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exportJob);

        // Remove the completed export job row. The export_manifests.export_job_id foreign key cascades on delete,
        // so the produced manifest is removed with the job (CORE-PRIV-003). The sweep already deleted the
        // object-storage artifact (if any) before this call, so no orphaned object remains. The audit log
        // references the job as a recorded fact (not a foreign key) and survives. A concurrent purge of the same
        // row makes this SaveChanges affect zero rows and throw DbUpdateConcurrencyException, which the sweep
        // treats as already-handled (concurrency-safe).
        _dbContext.ExportJobs.Remove(exportJob);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
