// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Exports;

/// <summary>
/// The minimal, generic coordinates of an export job that is QUEUED for the background export processing job
/// (CORE-JOB-002, the export-processing story of the "Worker Background Jobs" epic): a workspace-scoped
/// <see cref="ExportJob"/> that has not yet settled into a terminal state. It is the read-only projection
/// <see cref="IQueuedExportJobReader"/> returns to <see cref="ExportProcessingService"/>, carrying only what
/// the service needs to re-resolve and process that job — the tenant, workspace and job boundary ids.
///
/// It is a projection, NOT the <see cref="ExportJob"/> aggregate: the queued read returns only the
/// coordinates the processor needs to LOAD the tracked aggregate through the tenant-scoped
/// <see cref="IExportJobRepository.FindByIdAsync"/> (defence in depth — the processor never trusts the
/// surrogate id alone, it re-scopes the load by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/>,
/// threat T5 in docs/07_SECURITY_THREAT_MODEL.md). It carries only generic identifiers — never the requester,
/// the failure reason or any exported content (threats T7/T8).
/// </summary>
/// <param name="OrganizationId">Tenant the export job (and therefore its produced manifest) belongs to.</param>
/// <param name="WorkspaceId">Workspace the export job (and therefore its produced manifest) belongs to.</param>
/// <param name="ExportJobId">The queued export job to process.</param>
/// <param name="LeaseOwner">
/// The worker replica that currently holds the job's processing lease, or <see langword="null"/> when it is
/// unleased (CORE-RES-003). The processor passes this as the OBSERVED owner to the atomic compare-and-swap claim,
/// so a job is reclaimed only when its lease is still held by exactly the owner read here (or is still free).
/// </param>
/// <param name="LeasedUntil">
/// When the job's current lease expires (UTC), or <see langword="null"/> when it is unleased (CORE-RES-003). The
/// processor compares this against the current time IN MEMORY (never in SQL) to decide whether the job is free to
/// claim — see <see cref="IsClaimable"/>.
/// </param>
public readonly record struct QueuedExportJob(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ExportJobId,
    Guid? LeaseOwner,
    DateTimeOffset? LeasedUntil)
{
    /// <summary>
    /// Whether this queued job is free for a replica to CLAIM at <paramref name="now"/> (CORE-RES-003): true when
    /// it carries no lease or its lease has already EXPIRED (a crashed replica's lease is reclaimable), false
    /// while a live replica still holds an unexpired lease. The comparison is in memory — the SQLite test provider
    /// cannot compare a <see cref="DateTimeOffset"/> in SQL, so the queued read deliberately surfaces every
    /// non-terminal job WITH its lease state and the processor filters claimability here. The eventual atomicity
    /// (two replicas never both claim one job) comes from the set-based compare-and-swap claim, not from this
    /// best-effort pre-filter, which only avoids a doomed claim against an actively-leased job.
    /// </summary>
    /// <param name="now">The current UTC time the lease expiry is judged against.</param>
    public bool IsClaimable(DateTimeOffset now) => LeasedUntil is null || LeasedUntil.Value <= now;
}
