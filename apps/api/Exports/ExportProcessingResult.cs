namespace LiveCore.Api.Exports;

/// <summary>
/// The outcome of one run of the background export processing sweep (CORE-JOB-002). It records counts only —
/// never any identifier-free content — so the worker can log a sweep summary safely (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Examined">How many queued (workspace-scoped, non-terminal) export jobs the sweep looked at this run.</param>
/// <param name="Processed">
/// How many export jobs were processed to completion this run — started, inventoried, settled into
/// <see cref="ExportJobStatus.Completed"/> and given a workspace export <see cref="ExportManifest"/>. Because a
/// terminal job is never eligible and the unique <c>export_manifests(export_job_id)</c> index admits a job's
/// manifest at most once, a job produces exactly one manifest across all sweeps (idempotent).
/// </param>
/// <param name="Failed">
/// How many queued jobs could not be processed this run because a step failed (for example the job's workspace
/// was deleted between the queued read and processing, or a transient persistence error occurred). Those jobs
/// stay non-terminal — no manifest was committed — so the next sweep retries them; nothing is left in an
/// inconsistent state.
/// </param>
public readonly record struct ExportProcessingResult(int Examined, int Processed, int Failed);
