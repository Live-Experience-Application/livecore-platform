// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Observability;

namespace LiveCore.Worker;

/// <summary>
/// The stable, coarse OUTCOME values a background job loop tags its distributed-tracing span with
/// (CORE-OBS-012), recorded under <see cref="LiveCoreActivitySource.WorkerJobOutcomeTag"/> on the
/// <see cref="LiveCoreActivitySource.WorkerJobLoopActivityName"/> span each loop iteration produces. One value
/// per terminal state of a sweep, so a collector can tell a clean run from a failed one without inspecting any
/// content.
///
/// <para>
/// Concentrating the vocabulary here (as <see cref="WorkerJobNames"/> concentrates the loop names) keeps the
/// span CONTRACT in one place rather than letting each background service hard-code its own outcome literal.
/// The values are low-cardinality, non-sensitive and product-neutral — a coarse status word, never a
/// tenant/principal identifier or resource content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md, AGENTS.md).
/// </para>
/// </summary>
internal static class WorkerJobOutcomes
{
    /// <summary>The sweep completed without throwing.</summary>
    public const string Success = "success";

    /// <summary>The sweep threw; the loop logged it and will retry on the next interval.</summary>
    public const string Failure = "failure";

    /// <summary>The sweep was interrupted by host shutdown (cooperative cancellation).</summary>
    public const string Canceled = "canceled";
}
