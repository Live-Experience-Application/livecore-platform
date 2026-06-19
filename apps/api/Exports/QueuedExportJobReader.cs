// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Exports;

/// <summary>
/// EF Core implementation of <see cref="IQueuedExportJobReader"/> (CORE-JOB-002). It runs a single bounded
/// query over the Exports module's own <c>export_jobs</c> table: jobs of any scope that are not yet terminal,
/// ordered by the time-ordered surrogate job id.
/// </summary>
internal sealed class QueuedExportJobReader : IQueuedExportJobReader
{
    private readonly LiveCoreDbContext _dbContext;

    public QueuedExportJobReader(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedExportJob>> ListQueuedExportsAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The batch size must be positive.");
        }

        // Eligibility = a job of ANY scope that has NOT reached a terminal state. The read no longer filters on
        // the export scope: the processor branches on the loaded job's scope and produces the right artifact —
        // a workspace inventory manifest for a Workspace job, a terminal completion for a UserData job whose
        // personal-data export is assembled on download (CORE-EXP-002). Before CORE-EXP-002 only Workspace jobs
        // were surfaced, so a user-data export had no producer. The status predicate selects the two non-terminal
        // states (Pending or Running): a Pending job is freshly queued, and Running is included defensively so
        // any job left non-terminal by any cause is still picked up and driven to completion (an interrupted run
        // leaves the job Pending). A terminal (Completed/Failed) job is excluded, so a finished export is never
        // re-processed (idempotency; a Workspace job's produce-exactly-one-manifest guarantee is the unique
        // export_manifests(export_job_id) index, a UserData job's is its terminal status plus the worker
        // claim/lease). The order is the time-ordered surrogate job id (UUIDv7, chronological and
        // provider-independent — SQLite cannot ORDER BY a DateTimeOffset), so the oldest queued jobs sort first
        // and the bounded batch (Take(maxCount)) surfaces them, with repeated sweeps covering the rest. The
        // projection reads only the tenant/workspace/job coordinates the processor needs to re-resolve the job
        // and its LEASE STATE (CORE-RES-003) — never the requester or any content (threat T7).
        //
        // The read deliberately does NOT filter on the lease in SQL: a job's lease expiry is a DateTimeOffset,
        // which the SQLite test provider cannot compare in SQL (the same reason the retention reads order by id
        // and threshold in memory), and filtering by lease OWNER alone would hide an expired lease that must be
        // reclaimed (its owner is non-null). So every non-terminal job is surfaced WITH its lease coordinates and
        // the processor decides claimability in memory (QueuedExportJob.IsClaimable), then claims atomically; an
        // actively-leased job is simply skipped there. The lease owner is an opaque worker-replica id, never a
        // tenant, a user or content (threat T7).
        return await _dbContext.ExportJobs
            .Where(job => job.Status == ExportJobStatus.Pending || job.Status == ExportJobStatus.Running)
            .OrderBy(job => job.Id)
            .Take(maxCount)
            .Select(job => new QueuedExportJob(
                job.OrganizationId, job.WorkspaceId, job.Id, job.LeaseOwner, job.LeasedUntil))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
