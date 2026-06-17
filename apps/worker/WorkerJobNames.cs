// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Worker;

/// <summary>
/// The stable, coarse names of the worker's four background job loops (CORE-DR-003). One name per loop, used
/// as both the <c>job</c> tag on the <c>livecore_job_failures_total</c> metric (so a failing loop is
/// attributable on the scrape surface) and the key of each loop's own liveness heartbeat (so one hung loop is
/// detectable independently of the others — see <see cref="WorkerJobHeartbeats"/>).
///
/// <para>
/// Concentrating the names here keeps the metric/heartbeat CONTRACT in one place rather than letting each
/// background service hard-code its own string literal, so the factory that registers the active loops
/// (<see cref="WorkerHostFactory"/>) and the loops themselves can never drift. The names are low-cardinality,
/// non-sensitive and product-neutral (a coarse loop name, never a tenant identifier; threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md, AGENTS.md).
/// </para>
/// </summary>
internal static class WorkerJobNames
{
    /// <summary>The asset cleanup loop (CORE-AST-006).</summary>
    public const string AssetCleanup = "asset-cleanup";

    /// <summary>The recap generation loop (CORE-JOB-001).</summary>
    public const string RecapGeneration = "recap-generation";

    /// <summary>The export processing loop (CORE-JOB-002).</summary>
    public const string ExportProcessing = "export-processing";

    /// <summary>The billing-gated store-notification reconciliation loop (CORE-JOB-003).</summary>
    public const string StoreNotificationReconciliation = "store-notification-reconciliation";

    /// <summary>The data-retention sweep loop (CORE-PRIV-003).</summary>
    public const string DataRetention = "data-retention";
}
