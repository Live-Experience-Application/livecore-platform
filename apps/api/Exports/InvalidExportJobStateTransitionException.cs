// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Exports;

/// <summary>
/// Thrown when an <see cref="ExportJob"/> lifecycle transition (<see cref="ExportJob.Start"/>,
/// <see cref="ExportJob.Complete"/> or <see cref="ExportJob.Fail"/>) is attempted from a state in which it
/// is not legal (CORE-AUD-002). The export job state machine is
/// <see cref="ExportJobStatus.Pending"/> -&gt; <see cref="ExportJobStatus.Running"/> -&gt;
/// <see cref="ExportJobStatus.Completed"/> / <see cref="ExportJobStatus.Failed"/>; starting requires a
/// pending job, completing requires a running job, and failing requires a non-terminal (pending or
/// running) job. Every other attempt — in particular a transition out of a TERMINAL state — is a genuine
/// state error, not a no-op.
///
/// The throw is deliberate and chosen over a silent no-op: a finished export must never be silently
/// re-run or have its outcome overwritten, so an out-of-order transition surfaces as an error rather than
/// corrupting the recorded run. So the application layer can branch WITHOUT catching, the aggregate also
/// exposes the <see cref="ExportJob.CanStart"/>, <see cref="ExportJob.CanComplete"/> and
/// <see cref="ExportJob.CanFail"/> predicates; this exception is the fail-closed guard for callers that do
/// not pre-check (mirrors <see cref="Assets.InvalidAssetStateTransitionException"/>).
///
/// The message carries only the aggregate's id and the from/to status names (never the export scope, the
/// requester or any failure detail), so it is safe for structured logs (threats T7/T8 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class InvalidExportJobStateTransitionException : InvalidOperationException
{
    /// <summary>
    /// Creates a transition error describing the export job, the status it is in and the status the
    /// rejected transition targeted.
    /// </summary>
    public InvalidExportJobStateTransitionException(Guid exportJobId, ExportJobStatus from, ExportJobStatus to)
        : base($"Export job {exportJobId} cannot transition from {from} to {to}.")
    {
        ExportJobId = exportJobId;
        From = from;
        To = to;
    }

    /// <summary>The id of the export job whose transition was rejected.</summary>
    public Guid ExportJobId { get; }

    /// <summary>The status the export job was in when the transition was attempted.</summary>
    public ExportJobStatus From { get; }

    /// <summary>The status the rejected transition targeted.</summary>
    public ExportJobStatus To { get; }
}
