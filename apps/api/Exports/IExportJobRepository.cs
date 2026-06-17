namespace LiveCore.Api.Exports;

/// <summary>
/// Persistence contract for the export job aggregate (CORE-AUD-002). The Exports module owns the
/// <c>export_jobs</c> table; other modules access export jobs only through this contract or the module's
/// application services (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Exports module owns "export jobs").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the organization id and the
/// workspace id, and a job is only ever returned when it belongs to exactly that (organization, workspace)
/// pair. The organization boundary is checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md
/// authorization principles), so a job is never returned through a foreign organization's id even when the
/// workspace and ids are correct, and never through a foreign workspace's id even when the organization and
/// ids are correct. There is deliberately no lookup of a job by id alone, no lookup that crosses tenants and
/// NO list-everything read method, so one workspace's export job can never be read through another
/// workspace's id and a job in one tenant can never be read through another tenant's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization).
/// </summary>
public interface IExportJobRepository
{
    /// <summary>
    /// Finds the export job with exactly the given id WITHIN the given organization and workspace, or
    /// <see langword="null"/> when no such job exists there. The organization and workspace both scope the
    /// lookup, so a job that exists under another organization's or workspace's id is never returned, even
    /// when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or job id is empty. An empty id can never address a stored job, so
    /// the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<ExportJob?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the export job with exactly the given id WITHIN the given organization, or
    /// <see langword="null"/> when no such job exists there, discovering the job's own workspace from the
    /// loaded row. Unlike <see cref="FindByIdAsync"/> this lookup is scoped by the TENANT only, for the
    /// by-export-id read route whose path carries the export id but not the workspace (the export read/download
    /// endpoint, CORE-EXP-001): the predicate leads with <c>organization_id</c>, so a job under another tenant's
    /// id is never returned even when the surrogate id matches, and the caller then authorizes against the
    /// loaded job's OWN workspace AFTER the tenant boundary has been enforced — exactly the load-then-authorize
    /// shape <see cref="LiveCore.Api.Assets.IAssetRepository.FindByIdInOrganizationAsync"/> uses for the asset
    /// signed-download route (threat T5 tenant isolation; threat T1 broken object-level authorization).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or job id is empty. An empty id can never address a stored job, so the lookup is
    /// rejected instead of silently returning nothing.
    /// </exception>
    Task<ExportJob?> FindByIdInOrganizationAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every export job of the given workspace (owned by the given organization) in a deterministic
    /// order — sorted by the surrogate id, which is time-ordered (UUIDv7), so the sequence is stable and
    /// repeatable. The list is tenant- AND workspace-scoped: the predicate leads with <c>organization_id</c>
    /// and then matches <c>workspace_id</c>, so a foreign tenant's or a foreign workspace's jobs are NEVER
    /// returned even when their ids would otherwise be addressable (threat T5/T1; the organization boundary
    /// is checked before the workspace boundary). An empty list is returned for a workspace that has no
    /// jobs. This is NOT a list-everything method.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty. An empty id can never address a stored workspace's
    /// jobs, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<ExportJob>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new export job. A job has no natural key in this story (it is identified only by its
    /// surrogate id), so there is no uniqueness outcome to report; the result is always
    /// <see cref="ExportJobAddResult.Added"/> on success. Foreign-key violations (a non-existent workspace,
    /// tenant or requesting user) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<ExportJobAddResult> AddAsync(ExportJob exportJob, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to an export job previously loaded through this repository — in particular the
    /// lifecycle transitions that start, complete or fail the job. The organization, workspace, id,
    /// requester and scope of a job are immutable (<see cref="ExportJob"/>), so an update only ever changes
    /// the status, the failure reason and the update timestamp; it can never move the job to another tenant
    /// or workspace, nor widen its scope (threats T5/T8). The caller is responsible for having loaded the
    /// job through a tenant-scoped lookup.
    /// </summary>
    Task UpdateAsync(ExportJob exportJob, CancellationToken cancellationToken);

    /// <summary>
    /// Lists up to <paramref name="maxCount"/> COMPLETED export jobs created before <paramref name="createdBefore"/>
    /// — the candidates for the data-retention sweep (CORE-PRIV-003, GDPR Art.5(1)(e) storage limitation). This is
    /// a SYSTEM maintenance read that deliberately spans ALL tenants and workspaces (the retention sweep is a
    /// system job, not a tenant actor) and returns only <see cref="ExportJobStatus.Completed"/> jobs, so a
    /// still-pending, running or failed job is never a candidate. The retention window is measured from the job's
    /// CREATION time (its age); ordering is by the time-ordered surrogate id (UUIDv7, derived from the creation
    /// time), oldest first, and the creation-age threshold is applied after materialization (SQLite cannot ORDER
    /// BY or compare a DateTimeOffset), so the bounded batch surfaces the oldest completed jobs and over repeated
    /// sweeps every past-window job is covered without starvation. Each returned job carries its recorded artifact
    /// coordinates (<see cref="ExportJob.ArtifactBucket"/> / <see cref="ExportJob.ArtifactObjectKey"/>, when set)
    /// so the sweep can delete the object-storage blob before it removes the row.
    /// </summary>
    /// <param name="createdBefore">The exclusive creation-time threshold; only jobs created before it are returned.</param>
    /// <param name="maxCount">The maximum number of jobs a single sweep examines (a positive batch size).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="maxCount"/> is not positive.</exception>
    Task<IReadOnlyList<ExportJob>> ListCompletedForRetentionAsync(
        DateTimeOffset createdBefore,
        int maxCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES a completed export job row as part of the data-retention sweep (CORE-PRIV-003). The
    /// <c>export_manifests.export_job_id</c> foreign key cascades on delete, so the produced manifest is removed
    /// with the job; the sweep deletes any object-storage artifact (the blob at the job's recorded artifact
    /// coordinates) BEFORE calling this, so a removed export never leaves an orphaned object. The <c>audit_logs</c>
    /// references the job as a recorded fact, not a foreign key, so the audit trail SURVIVES the purge. The caller
    /// must have re-loaded the job through a tenant-scoped lookup inside the purge transaction; a concurrent purge
    /// of the same row surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> which
    /// the sweep treats as already-handled (concurrency-safe).
    /// </summary>
    Task DeleteAsync(ExportJob exportJob, CancellationToken cancellationToken);
}
