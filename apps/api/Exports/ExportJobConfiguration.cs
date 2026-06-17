using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Exports;

/// <summary>
/// Relational mapping of <see cref="ExportJob"/> to the <c>export_jobs</c> table (CORE-AUD-002; the
/// Exports module's first table; csv/database_tables.csv: module Exports, scope <c>workspace</c>, "Async
/// exports"). The export job is workspace-scoped, so it carries a <c>workspace_id</c> column that is a
/// foreign key into <c>workspaces(id)</c>, the documented <c>export_jobs(workspace_id, id)</c> shape
/// (docs/10_DATABASE_SCHEMA.md critical indexes). It is also tenant-scoped, so it carries an
/// <c>organization_id</c> column that is a foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped tables include <c>organization_id</c>"; threat T5
/// in docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins the organization, but storing
/// the tenant on the row lets isolation be enforced at the row level and lets the organization boundary be
/// checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles),
/// mirroring <c>assets</c>.
///
/// The optional <c>requested_by</c> column is a NULLABLE foreign key into <c>users(id)</c>: an export is
/// always requested by an authenticated user, but the link SETS NULL when that user is deleted so the job
/// (the audit trail of who requested an export) survives and is anonymized rather than cascade-deleted
/// (mirrors <c>assets.created_by</c> and <c>participants.user_id</c>). The nullable <c>failure_reason</c>
/// column is null unless the job failed.
///
/// Two indexes back the documented access patterns:
/// <list type="bullet">
///   <item>The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the documented critical
///   index <c>export_jobs(workspace_id, id)</c>: it keeps workspace-scoped lookups by id efficient and
///   makes every export-job read lead with the workspace column (docs/10_DATABASE_SCHEMA.md).</item>
///   <item>The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the
///   tenant boundary check (checked before the workspace boundary) and the organization foreign-key check
///   efficient and makes tenant-scoped reads lead with <c>organization_id</c> (threat T5).</item>
/// </list>
/// There is deliberately NO unique natural-key index: an export job is identified only by its surrogate id
/// (a workspace may have many export jobs, including repeats of the same scope).
///
/// The tenant and workspace foreign keys cascade on delete (an export job has no meaning without its
/// tenant or workspace, so removing either removes its jobs — mirrors <c>assets</c>/<c>sessions</c>); the
/// <c>requested_by</c> user foreign key sets null on delete (the record of an export is retained when its
/// requester is removed — mirrors <c>assets.created_by</c>).
///
/// The <c>scope</c> and <c>status</c> columns store the enum NAME (Workspace/UserData and
/// Pending/Running/Completed/Failed), persisted via <c>HasConversion&lt;string&gt;</c> exactly like
/// <c>assets.status</c> and <c>sessions.status</c>, so the stored values stay readable and are not coupled
/// to the declaration-order integers. The <c>failure_reason</c> is a bounded, single-line diagnostic and
/// never carries exported content (threats T7/T8 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// Deployment requirement: equality on the id columns is binary, so no collation concern applies. Pinning
/// any column collation, if ever needed, is planned with the first story that runs migrations against real
/// PostgreSQL, exactly as noted for the <c>assets</c>, <c>sessions</c> and <c>entities</c> tables.
/// </summary>
internal sealed class ExportJobConfiguration : IEntityTypeConfiguration<ExportJob>
{
    public void Configure(EntityTypeBuilder<ExportJob> builder)
    {
        builder.ToTable("export_jobs");

        builder.HasKey(job => job.Id);

        builder.Property(job => job.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(job => job.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(job => job.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(job => job.RequestedByUserProfileId)
            .HasColumnName("requested_by")
            .IsRequired(false);

        builder.Property(job => job.Scope)
            .HasColumnName("scope")
            // Persist the scope as its stable name (not its numeric value) so the stored value stays
            // readable and is not coupled to the declaration-order integers.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(job => job.Status)
            .HasColumnName("status")
            // Persist the status as its stable name (not its numeric value), mirrors the other lifecycle
            // enums (assets.status, sessions.status).
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        // Null unless the job failed; a bounded, single-line diagnostic, never exported content.
        builder.Property(job => job.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(ExportJob.MaxFailureReasonLength)
            .IsRequired(false);

        builder.Property(job => job.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(job => job.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Optional object-storage coordinates of the completed export's downloadable artifact (its produced
        // blob), recorded by ExportJob.RecordArtifact so the data-retention sweep (CORE-PRIV-003) can purge the
        // object with the row. NULLABLE because Core's manifest-only export pipeline writes no blob (the artifact
        // is the export_manifests row); both columns are storage NAMING only, never exported content or a
        // credential (threats T4/T7), and are not indexed (they are never a lookup key, only a purge coordinate).
        builder.Property(job => job.ArtifactBucket)
            .HasColumnName("artifact_bucket")
            .HasMaxLength(ExportJob.MaxArtifactBucketLength)
            .IsRequired(false);

        builder.Property(job => job.ArtifactObjectKey)
            .HasColumnName("artifact_object_key")
            .HasMaxLength(ExportJob.MaxArtifactObjectKeyLength)
            .IsRequired(false);

        // Documented critical index export_jobs(workspace_id, id): workspace reads lead with the workspace
        // column (docs/10_DATABASE_SCHEMA.md critical indexes).
        builder.HasIndex(job => new { job.WorkspaceId, job.Id })
            .HasDatabaseName("ix_export_jobs_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization boundary
        // check (checked before the workspace boundary) and tenant-scoped reads efficient
        // (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(job => new { job.OrganizationId, job.WorkspaceId })
            .HasDatabaseName("ix_export_jobs_organization_id_workspace_id");

        // Tenant foreign key: every export job hangs off exactly one organization (the owner of its
        // workspace). Cascade delete removes jobs with their tenant (a job has no meaning without its
        // tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(job => job.OrganizationId)
            .HasConstraintName("fk_export_jobs_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every export job hangs off exactly one workspace. Cascade delete removes
        // jobs with their workspace (a job has no meaning without its workspace).
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(job => job.WorkspaceId)
            .HasConstraintName("fk_export_jobs_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Optional requester foreign key: a job may reference at most one user profile as its requester.
        // Set null on delete so removing the user anonymizes the job record rather than deleting the job
        // (the audit trail of who requested an export survives; mirrors assets.created_by).
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(job => job.RequestedByUserProfileId)
            .HasConstraintName("fk_export_jobs_users_requested_by")
            .OnDelete(DeleteBehavior.SetNull);
    }
}
