// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
/// was deleted between the queued read and processing, or a transient persistence error occurred). Each failed
/// attempt is recorded on the job's attempt counter; the job stays non-terminal — no manifest was committed —
/// so the next sweep retries it, UNLESS the failure exhausted its attempt budget, in which case it is counted
/// in <see cref="DeadLettered"/> instead. Nothing is left in an inconsistent state.
/// </param>
/// <param name="DeadLettered">
/// How many queued jobs reached the configured maximum number of failed attempts this run and were
/// DEAD-LETTERED — driven to the terminal <see cref="ExportJobStatus.Failed"/> state via
/// <see cref="ExportJob.Fail"/> instead of being retried again (CORE-RES-002). A dead-lettered job is terminal,
/// so it is no longer queued: it stops re-consuming a batch slot and newer work progresses past it, and the
/// broken export surfaces as failed to its requester.
/// </param>
public readonly record struct ExportProcessingResult(int Examined, int Processed, int Failed, int DeadLettered);
