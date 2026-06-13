using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Exports;

/// <summary>
/// The background export processing job's application service (CORE-JOB-002, the export-processing story of the
/// "Worker Background Jobs" epic). It processes queued export jobs asynchronously into workspace export
/// manifests — picking up the workspace-scoped jobs that have not yet finished, driving each through its guarded
/// status transitions and producing its <see cref="ExportManifest"/> — on the worker's configurable cadence,
/// idempotently and tenant-scoped (the acceptance criterion: "Export jobs are processed asynchronously by the
/// worker into manifests, authorized and tenant-scoped, with status transitions"). The scheduling host — the
/// background worker — invokes this service on an interval (docs/02_ARCHITECTURE.md: the worker owns async
/// jobs), exactly as it invokes the recap generation and asset cleanup services; the service itself is
/// host-agnostic and fully unit-testable.
///
/// <para>
/// It REUSES the existing Exports module (the story note): the queued jobs come from
/// <see cref="IQueuedExportJobReader"/>, each job is loaded and its transitions persisted through the existing
/// <see cref="IExportJobRepository"/>, the workspace is inventoried through
/// <see cref="IWorkspaceExportInventoryReader"/>, and the produced manifest is built by the existing
/// <see cref="ExportManifest.ForWorkspaceExport"/> factory and appended through the existing
/// <see cref="IExportManifestRepository"/>. No parallel export pipeline is built.
/// </para>
///
/// <para>
/// STATUS TRANSITIONS (the acceptance criterion). A queued job is driven through the
/// <see cref="ExportJob"/> aggregate's own guarded state machine — <see cref="ExportJob.Start"/>
/// (<see cref="ExportJobStatus.Pending"/> -&gt; <see cref="ExportJobStatus.Running"/>) then
/// <see cref="ExportJob.Complete"/> (<see cref="ExportJobStatus.Running"/> -&gt;
/// <see cref="ExportJobStatus.Completed"/>) — and those transitions are committed ATOMICALLY with the produced
/// manifest as a single unit of work (the repositories share one EF unit of work per sweep scope, so the
/// tracked job's status change flushes in the same transaction as the manifest insert). Every transition is
/// guarded by the aggregate, so a finished export can never be silently re-run, and the job is never observed
/// in a half-finished persisted state.
/// </para>
///
/// <para>
/// IDEMPOTENT and CRASH-SAFE. The processor relies on the existing data-layer invariants rather than a parallel
/// lock: the queued read excludes terminal jobs (a completed export is never re-processed), and the unique
/// <c>export_manifests(export_job_id)</c> index admits a job's manifest at most once. So a job produces exactly
/// one manifest no matter how many sweeps — or concurrent workers — run: a lost create race surfaces as
/// <see cref="ExportManifestAddResult.DuplicateExportJob"/>, and because the manifest and the status transitions
/// commit atomically, the losing attempt's transitions roll back WITH the rejected insert (the job is left
/// exactly as the winning attempt completed it, never half-applied). A crash BEFORE the commit leaves the job
/// untouched (Pending), so the next sweep simply reprocesses it from scratch.
/// </para>
///
/// <para>
/// TENANT SCOPING (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The sweep spans all tenants on purpose (it
/// is a system job, not a tenant actor), but each job is re-loaded through the tenant-scoped
/// <see cref="IExportJobRepository.FindByIdAsync"/> (the surrogate id is never trusted alone — it is re-scoped
/// by the job's own organization and workspace, the organization boundary checked first), the workspace is
/// inventoried with exactly that job's organization and workspace, and the manifest is produced with exactly
/// those, so one workspace's export only ever counts its own resources and its manifest is attributed only to
/// its own tenant/workspace — never another's. Scope is honoured too: the reader surfaces only
/// <see cref="ExportScope.Workspace"/> jobs and the processor re-checks the loaded job's scope, so a user-data
/// export is never widened into a workspace inventory (threat T8).
/// </para>
///
/// <para>
/// RESILIENCE. The sweep is per-job resilient: a job whose processing fails (for example a foreign-key
/// violation because its workspace was deleted between the queued read and processing, or a transient
/// persistence error) is logged and counted, and that job is left non-terminal — no manifest was committed — so
/// the next sweep retries it, rather than aborting the whole run. All logging is identifier-only (job and
/// manifest ids and counts, never the requester or any content), so a sweep is safe to log (threat T7).
/// </para>
/// </summary>
public sealed class ExportProcessingService
{
    private readonly IQueuedExportJobReader _queuedExports;
    private readonly IExportJobRepository _exportJobs;
    private readonly IWorkspaceExportInventoryReader _inventory;
    private readonly IExportManifestRepository _exportManifests;
    private readonly ExportProcessingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExportProcessingService> _logger;

    /// <summary>Creates the processing service over the queued read, the export job and manifest repositories, the inventory read and the policy.</summary>
    public ExportProcessingService(
        IQueuedExportJobReader queuedExports,
        IExportJobRepository exportJobs,
        IWorkspaceExportInventoryReader inventory,
        IExportManifestRepository exportManifests,
        ExportProcessingOptions options,
        TimeProvider timeProvider,
        ILogger<ExportProcessingService> logger)
    {
        ArgumentNullException.ThrowIfNull(queuedExports);
        ArgumentNullException.ThrowIfNull(exportJobs);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(exportManifests);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _queuedExports = queuedExports;
        _exportJobs = exportJobs;
        _inventory = inventory;
        _exportManifests = exportManifests;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one processing sweep: finds up to <see cref="ExportProcessingOptions.BatchSize"/> queued export
    /// jobs (workspace-scoped, not terminal), processes each to completion (started, inventoried, completed and
    /// given a workspace export manifest) and returns the run's count-only summary. Idempotent (a terminal job
    /// is never eligible and a job's manifest is admitted at most once, so a job produces exactly one manifest)
    /// and tenant-scoped (each job is re-resolved, inventoried and manifested with its own
    /// tenant/workspace). The sweep is resilient: a per-job failure is logged and that job is left for the next
    /// sweep to retry, rather than aborting the run.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <returns>A summary of how many jobs were examined, processed and failed.</returns>
    public async Task<ExportProcessingResult> ProcessQueuedExportsAsync(CancellationToken cancellationToken)
    {
        var queued = await _queuedExports
            .ListQueuedWorkspaceExportsAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (queued.Count == 0)
        {
            return new ExportProcessingResult(Examined: 0, Processed: 0, Failed: 0);
        }

        var examined = 0;
        var processed = 0;
        var failed = 0;

        foreach (var queuedJob in queued)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            try
            {
                if (await ProcessOneAsync(queuedJob, cancellationToken).ConfigureAwait(false))
                {
                    processed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown during the sweep is expected; stop cleanly and let the loop end.
                throw;
            }
            catch (DbUpdateException exception)
            {
                // A step failed to persist — the job's workspace/tenant may have been removed between the queued
                // read and processing (a foreign-key violation), or a transient persistence error occurred. No
                // manifest was committed and the job is left non-terminal, so the next sweep retries it, and we
                // move on. The message carries only the job id, never the requester or any content (threat T7).
                failed++;
                _logger.LogWarning(
                    exception,
                    "Failed to process queued export job {ExportJobId}; it stays queued and will be retried on the next sweep.",
                    queuedJob.ExportJobId);
            }
        }

        return new ExportProcessingResult(Examined: examined, Processed: processed, Failed: failed);
    }

    /// <summary>
    /// Processes a single queued export job to completion: re-resolves it within its own tenant/workspace,
    /// drives its guarded <see cref="ExportJob.Start"/>/<see cref="ExportJob.Complete"/> transitions, inventories
    /// its workspace and produces and appends its workspace export manifest. Returns whether a job was processed
    /// (false when the job has vanished, is already terminal, or is — defensively — not workspace-scoped).
    /// </summary>
    private async Task<bool> ProcessOneAsync(QueuedExportJob queuedJob, CancellationToken cancellationToken)
    {
        // One processing timestamp for the whole job, taken from the injected clock so the behavior is
        // deterministic under test (the same TimeProvider the rest of the platform uses).
        var processedAt = _timeProvider.GetUtcNow();

        // Re-resolve the tracked aggregate, re-scoped by tenant + workspace (defence in depth: the surrogate id
        // is never trusted alone, threat T5/T1). The organization boundary is checked before the workspace
        // boundary inside the repository.
        var job = await _exportJobs
            .FindByIdAsync(queuedJob.OrganizationId, queuedJob.WorkspaceId, queuedJob.ExportJobId, cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            // The job vanished between the queued read and now (for example its workspace was deleted). Nothing
            // to process.
            return false;
        }

        if (job.IsTerminal)
        {
            // A concurrent worker already finished (or failed) it between the queued read and the load;
            // idempotent no-op.
            return false;
        }

        if (job.Scope != ExportScope.Workspace)
        {
            // The reader only surfaces workspace-scoped jobs; this is defence in depth. A user-data export's
            // narrower manifest is a later story, never widened into a workspace inventory here (threat T8).
            return false;
        }

        // Drive the guarded status transitions on the aggregate: Pending -> Running -> Completed. They are
        // applied IN MEMORY and committed ATOMICALLY with the produced manifest by the single unit of work
        // below (the repositories share one EF DbContext per sweep scope). Start is guarded so a job re-surfaced
        // while already Running skips straight to completing; a job that is Pending transitions through Running
        // to Completed.
        if (job.CanStart)
        {
            job.Start(processedAt);
        }

        // Inventory the workspace — counts only, scoped to the job's OWN tenant and workspace, so one
        // workspace's export never counts another's resources (threat T5) and no content is read (threats
        // T7/T8).
        var inventory = await _inventory
            .CountWorkspaceResourcesAsync(job.OrganizationId, job.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        // Status transition Running -> Completed (in memory) so the produced manifest can be built from the
        // completed job (the factory accepts a completed, workspace-scoped job only).
        job.Complete(processedAt);
        var manifest = ExportManifest.ForWorkspaceExport(job, inventory, processedAt);

        // Appending the manifest is the SINGLE unit of work that commits both the new manifest and the job's
        // status transitions together (the shared DbContext flushes the tracked job's Running -> Completed
        // change in the same SaveChanges as the manifest insert). So the job and its manifest are made durable
        // ATOMICALLY: a job is never observed completed without its manifest, nor a manifest without a completed
        // job, and a crash before this commit simply leaves the job queued (Pending) for the next sweep to
        // reprocess.
        //
        // The unique export_manifests(export_job_id) index makes this idempotent under retries and concurrent
        // workers: a run that already produced the manifest is reported as a DUPLICATE, and because the commit
        // is atomic the losing attempt's status transitions roll back WITH it — the job is left exactly as the
        // winning attempt completed it, never half-applied. Any other persistence failure surfaces as a
        // DbUpdateException, caught one level up so the job stays queued for the next sweep.
        var addResult = await _exportManifests.AddAsync(manifest, cancellationToken).ConfigureAwait(false);

        if (addResult == ExportManifestAddResult.Added)
        {
            _logger.LogInformation(
                "Processed workspace export job {ExportJobId} into manifest {ManifestId}: {ItemCount} items across {KindCount} kinds.",
                job.Id,
                manifest.Id,
                manifest.TotalItemCount,
                manifest.Entries.Count);
        }
        else
        {
            // DuplicateExportJob: a concurrent worker or an earlier run already produced this job's manifest and
            // completed it; this attempt's transitions rolled back with the rejected insert, so the job stays
            // completed with its single existing manifest. Identifier-only log.
            _logger.LogInformation(
                "Queued export job {ExportJobId} was already processed by another run; left its existing manifest in place.",
                job.Id);
        }

        return true;
    }
}
