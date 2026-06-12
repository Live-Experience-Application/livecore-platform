namespace LiveCore.Api.Exports;

/// <summary>
/// Outcome of persisting a new export manifest (CORE-AUD-003).
///
/// A manifest has a per-job natural key — exactly one manifest is ever produced per export job (the unique
/// <c>export_manifests(export_job_id)</c> index) — so an insert can report either success or that the job
/// already has a manifest. This mirrors <c>AssetLinkAddResult</c> (Added / Duplicate). Foreign-key
/// violations (a non-existent job, workspace or tenant) surface as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as a result
/// value.
/// </summary>
public enum ExportManifestAddResult
{
    /// <summary>The export manifest was persisted.</summary>
    Added = 1,

    /// <summary>
    /// The export job already has a manifest; the insert was rejected by the unique
    /// <c>export_manifests(export_job_id)</c> index (a manifest is produced once per job). No second manifest
    /// is created.
    /// </summary>
    DuplicateExportJob = 2,
}
