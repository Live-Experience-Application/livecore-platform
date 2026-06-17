// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Exports;

/// <summary>
/// EF Core implementation of <see cref="IExportManifestRepository"/> (CORE-AUD-003), backed by the
/// <c>export_manifests</c> table (with its <c>export_manifest_entries</c> child rows) mapped in
/// <see cref="ExportManifestConfiguration"/> and <see cref="ExportManifestEntryConfiguration"/>.
/// </summary>
internal sealed class ExportManifestRepository : IExportManifestRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public ExportManifestRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<ExportManifest?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored manifest (ids are generated non-empty), so the lookup fails
        // fast instead of returning an arbitrary row.
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
            throw new ArgumentException("Export manifest id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant column. The
        // lookup is exactly tenant- and workspace-scoped, so a manifest under another organization or
        // workspace is never returned even when the surrogate id matches (threat T5/T1). The inventory
        // entries are loaded with the manifest.
        return await _dbContext.ExportManifests
            .Include(manifest => manifest.Entries)
            .FirstOrDefaultAsync(
                manifest => manifest.OrganizationId == organizationId
                    && manifest.WorkspaceId == workspaceId
                    && manifest.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExportManifest?> FindByExportJobIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid exportJobId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (exportJobId == Guid.Empty)
        {
            throw new ArgumentException("Export job id must not be empty.", nameof(exportJobId));
        }

        // The predicate leads with the tenant column, then matches the workspace and the job, so the lookup
        // is exactly tenant- and workspace-scoped: another tenant's or another workspace's manifest is never
        // returned even when the job id matches (threat T5/T1).
        return await _dbContext.ExportManifests
            .Include(manifest => manifest.Entries)
            .FirstOrDefaultAsync(
                manifest => manifest.OrganizationId == organizationId
                    && manifest.WorkspaceId == workspaceId
                    && manifest.ExportJobId == exportJobId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExportManifestAddResult> AddAsync(ExportManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        _dbContext.ExportManifests.Add(manifest);
        try
        {
            // The manifest and its inventory entries are saved as one graph; EF inserts the parent row and
            // the child entry rows in one transaction.
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ExportManifestAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the
            // same scope.
            _dbContext.Entry(manifest).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if the job already has a manifest, the unique
            // export_manifests(export_job_id) index rejected the insert (a manifest is produced once per
            // job — typically a lost create race). Any other failure (for example a foreign-key violation
            // for a non-existent job, workspace or tenant) is rethrown unchanged.
            var duplicateExists = await _dbContext.ExportManifests
                .AsNoTracking()
                .AnyAsync(existing => existing.ExportJobId == manifest.ExportJobId, cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return ExportManifestAddResult.DuplicateExportJob;
            }

            throw;
        }
    }
}
