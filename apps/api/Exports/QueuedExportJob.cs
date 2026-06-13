namespace LiveCore.Api.Exports;

/// <summary>
/// The minimal, generic coordinates of an export job that is QUEUED for the background export processing job
/// (CORE-JOB-002, the export-processing story of the "Worker Background Jobs" epic): a workspace-scoped
/// <see cref="ExportJob"/> that has not yet settled into a terminal state. It is the read-only projection
/// <see cref="IQueuedExportJobReader"/> returns to <see cref="ExportProcessingService"/>, carrying only what
/// the service needs to re-resolve and process that job — the tenant, workspace and job boundary ids.
///
/// It is a projection, NOT the <see cref="ExportJob"/> aggregate: the queued read returns only the
/// coordinates the processor needs to LOAD the tracked aggregate through the tenant-scoped
/// <see cref="IExportJobRepository.FindByIdAsync"/> (defence in depth — the processor never trusts the
/// surrogate id alone, it re-scopes the load by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/>,
/// threat T5 in docs/07_SECURITY_THREAT_MODEL.md). It carries only generic identifiers — never the requester,
/// the failure reason or any exported content (threats T7/T8).
/// </summary>
/// <param name="OrganizationId">Tenant the export job (and therefore its produced manifest) belongs to.</param>
/// <param name="WorkspaceId">Workspace the export job (and therefore its produced manifest) belongs to.</param>
/// <param name="ExportJobId">The queued export job to process.</param>
public readonly record struct QueuedExportJob(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ExportJobId);
