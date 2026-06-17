// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Exports;

/// <summary>
/// System read for the background export processing job (CORE-JOB-002): it returns the export jobs that NEED
/// processing — workspace-scoped jobs that have not yet reached a terminal state (still
/// <see cref="ExportJobStatus.Pending"/> or <see cref="ExportJobStatus.Running"/>). It is the eligibility half
/// of the job, separate from the tenant-scoped <see cref="IExportJobRepository"/> (whose reads and writes are
/// always scoped to a single (organization, workspace) pair), because this is a SYSTEM sweep that deliberately
/// spans all tenants and workspaces, exactly as the recap generation job's
/// <see cref="LiveCore.Api.Recaps.IRecapEligibleSessionReader"/> and the asset cleanup job's <c>ListExpiredPendingAsync</c> are
/// cross-tenant system reads.
///
/// <para>
/// SCOPE = WORKSPACE ONLY. The reader surfaces only <see cref="ExportScope.Workspace"/> jobs, because the
/// produced artifact of this story is the workspace export <see cref="ExportManifest"/> (its
/// <see cref="ExportManifest.ForWorkspaceExport"/> factory accepts a workspace-scoped job only). A user-data
/// export's narrower manifest is a separate artifact that does not yet exist (CORE-AUD-003 deferred it
/// explicitly), so processing a <see cref="ExportScope.UserData"/> job is a follow-up story — never silently
/// widened into a workspace inventory here (threat T8 "explicit host/admin export scopes").
/// </para>
///
/// <para>
/// IDEMPOTENCY AND RECOVERY. Returning every NON-TERMINAL job makes the job both idempotent and
/// crash-recoverable. A terminal (<see cref="ExportJobStatus.Completed"/> or
/// <see cref="ExportJobStatus.Failed"/>) job is never returned, so a finished export is never re-processed. The
/// processor commits a job's status transitions atomically with its produced manifest, so an interrupted run
/// leaves the job exactly as it found it (still <see cref="ExportJobStatus.Pending"/>) and the next sweep simply
/// reprocesses it; <see cref="ExportJobStatus.Running"/> is included in the eligibility defensively, so any job
/// left non-terminal by any cause is still picked up and driven to completion. The ultimate
/// produce-exactly-one-manifest guarantee is the unique <c>export_manifests(export_job_id)</c> index, which the
/// processor relies on (a duplicate add rolls the losing attempt back), so a job is processed to a single
/// manifest no matter how many sweeps or workers run.
/// </para>
///
/// <para>
/// MODULE BOUNDARY. The read uses only the Exports module's own <c>export_jobs</c> table, so it is wholly
/// within the module (docs/05_MODULE_CONTRACTS.md: the Exports module owns "export jobs"). It only READS, never
/// writes (the processor performs the guarded status transitions through <see cref="IExportJobRepository"/>).
/// </para>
///
/// TENANT SAFETY. The sweep returns jobs across every tenant on purpose (it is a system job, not a tenant
/// actor), but every returned <see cref="QueuedExportJob"/> carries its OWN job's organization, workspace and
/// id, so the manifest the service produces is scoped to exactly that tenant/workspace and can never be
/// attributed to another tenant (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The read returns only generic
/// identifiers — never the requester, the failure reason or any content (threats T7/T8).
/// </summary>
public interface IQueuedExportJobReader
{
    /// <summary>
    /// Lists up to <paramref name="maxCount"/> export jobs that need processing — workspace-scoped jobs that
    /// are still <see cref="ExportJobStatus.Pending"/> or <see cref="ExportJobStatus.Running"/> — in a
    /// deterministic order (by the time-ordered surrogate job id, UUIDv7, which is chronological and
    /// provider-independent), so the OLDEST queued jobs are processed first and repeated sweeps eventually
    /// cover them all. The list spans every tenant and workspace (a system sweep), but each entry carries its
    /// own tenant/workspace/job coordinates.
    /// </summary>
    /// <param name="maxCount">The maximum number of queued jobs to return (the sweep batch size).</param>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <exception cref="System.ArgumentOutOfRangeException">The batch size is not positive.</exception>
    Task<IReadOnlyList<QueuedExportJob>> ListQueuedWorkspaceExportsAsync(
        int maxCount,
        CancellationToken cancellationToken);
}
