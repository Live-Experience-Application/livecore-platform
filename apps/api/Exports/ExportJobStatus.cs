// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Exports;

/// <summary>
/// Lifecycle status of an <see cref="ExportJob"/> (CORE-AUD-002, the first story of the "Audit, Export and
/// Recap" epic). An export is an ASYNC job (csv/database_tables.csv: table <c>export_jobs</c>, module
/// Exports, scope <c>workspace</c>, "Async exports"; docs/02_ARCHITECTURE.md: <c>apps/worker</c> owns
/// "background jobs, exports, cleanup, async processing"), so its status is the state of that asynchronous
/// run.
///
/// The four states form a small, guarded state machine expressed by the <see cref="ExportJob"/> aggregate
/// (not by integer order): a job is created <see cref="Pending"/> (queued), a worker
/// <see cref="ExportJob.Start"/>s it (<see cref="Pending"/> -&gt; <see cref="Running"/>), and it then
/// settles into exactly one TERMINAL state — <see cref="Completed"/> on success
/// (<see cref="ExportJob.Complete"/>) or <see cref="Failed"/> on error (<see cref="ExportJob.Fail"/>). A
/// terminal job never transitions again, so a finished export can never be silently re-run or overwritten.
/// The worker that actually drives these transitions, and the produced export MANIFEST, are later stories
/// (the manifest is CORE-AUD-003); this story models only the job and its lifecycle, exactly as
/// CORE-AST-001 modeled the asset metadata and its lifecycle without the upload flow.
///
/// The status is persisted by its stable NAME (not its numeric value), so the integers below are only
/// in-memory storage discriminators (persisted by name, like <c>AssetStatus</c>, <c>SessionStatus</c> and
/// every other enum in the model), carry no ordering meaning and must not be compared with &gt;/&lt;.
/// </summary>
public enum ExportJobStatus
{
    /// <summary>
    /// The export job has been requested and queued but a worker has not started processing it yet. The
    /// only state from which the job may be <see cref="ExportJob.Start"/>ed. A pending job may also
    /// <see cref="ExportJob.Fail"/> directly (for example, rejected before processing begins).
    /// </summary>
    Pending = 1,

    /// <summary>
    /// A worker is actively producing the export. Reached from <see cref="Pending"/> via
    /// <see cref="ExportJob.Start"/>. The only state from which the job may be
    /// <see cref="ExportJob.Complete"/>d, and one of the two states from which it may
    /// <see cref="ExportJob.Fail"/>.
    /// </summary>
    Running = 2,

    /// <summary>
    /// The export finished successfully. TERMINAL: a completed job never transitions again (the produced
    /// manifest is recorded by the later manifest story, CORE-AUD-003).
    /// </summary>
    Completed = 3,

    /// <summary>
    /// The export did not finish successfully and carries a generic failure reason (never exported
    /// content; threats T7/T8 in docs/07_SECURITY_THREAT_MODEL.md). TERMINAL: a failed job never
    /// transitions again; a retry is a NEW job, not a revival of this one.
    /// </summary>
    Failed = 4,
}
