// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Exports;

/// <summary>
/// Relational mapping of <see cref="ExportManifest"/> to the <c>export_manifests</c> table (CORE-AUD-003;
/// the Exports module's second table; docs/05_MODULE_CONTRACTS.md: the Exports module owns "export
/// manifests"). The manifest is the produced TABLE OF CONTENTS of a completed workspace export: a row records
/// which <c>export_job</c> produced it, the tenant and workspace it belongs to, the explicit export
/// <c>scope</c>, the manifest <c>manifest_version</c> and the <c>generated_at</c> timestamp; its per-kind
/// inventory lives in the <c>export_manifest_entries</c> child table (<see cref="ExportManifestEntryConfiguration"/>).
///
/// The manifest is workspace-scoped, so it carries a <c>workspace_id</c> column that is a foreign key into
/// <c>workspaces(id)</c>, the documented <c>export_jobs(workspace_id, id)</c> shape extended to manifests; it
/// is also tenant-scoped, so it carries an <c>organization_id</c> column that is a foreign key into
/// <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped tables include
/// <c>organization_id</c>"; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins
/// the organization, but storing the tenant on the row lets isolation be enforced at the row level and the
/// organization boundary be checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md
/// authorization principles), mirroring <c>export_jobs</c>.
///
/// The <c>export_job_id</c> column is a foreign key into <c>export_jobs(id)</c> with a UNIQUE index: exactly
/// one manifest is produced per job, so a job can never accumulate two manifests. The <c>scope</c> column
/// stores the enum NAME (Workspace), persisted via <c>HasConversion&lt;string&gt;</c> exactly like
/// <c>export_jobs.scope</c>.
///
/// Indexes back the documented access patterns and the per-job uniqueness invariant:
/// <list type="bullet">
///   <item>The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) keeps workspace-scoped lookups
///   by id efficient and makes every manifest read lead with the workspace column (docs/10_DATABASE_SCHEMA.md,
///   the <c>export_manifests(workspace_id, id)</c> critical index).</item>
///   <item>The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant
///   boundary check (checked before the workspace boundary) and the organization foreign-key check efficient
///   (threat T5).</item>
///   <item>The UNIQUE index on (<c>export_job_id</c>) is the per-job natural key — one manifest per export
///   job — and backs the find-by-job lookup. EF additionally indexes the <c>workspace_id</c> and
///   <c>organization_id</c> foreign-key columns.</item>
/// </list>
///
/// FOREIGN KEYS / CASCADE. The tenant, workspace and producing-job foreign keys cascade on delete (a manifest
/// has no meaning without its tenant, its workspace or its job — mirrors how <c>export_jobs</c> cascade with
/// their tenant/workspace). The inventory entries cascade with the manifest (the relationship below). A
/// manifest is WRITE-ONCE — there is no update or delete path on the aggregate or repository — exactly as the
/// append-only audit log is immutable (docs/10_DATABASE_SCHEMA.md).
///
/// The per-kind inventory is mapped as a one-to-many relationship to <see cref="ExportManifestEntry"/> over
/// the aggregate's read-only <see cref="ExportManifest.Entries"/> collection (its backing field), with the
/// child's <c>export_manifest_id</c> foreign key cascading on delete so a manifest's entries are removed with
/// it. No exported content is ever stored — only identifiers, the scope, a version, a timestamp and per-kind
/// counts (threats T7/T8).
///
/// Deployment requirement: equality on the id/enum columns is binary, so no collation concern applies.
/// Pinning any column collation, if ever needed, is planned with the first story that runs migrations against
/// real PostgreSQL, exactly as noted for the <c>export_jobs</c>, <c>assets</c> and other workspace-scoped
/// tables.
/// </summary>
internal sealed class ExportManifestConfiguration : IEntityTypeConfiguration<ExportManifest>
{
    public void Configure(EntityTypeBuilder<ExportManifest> builder)
    {
        builder.ToTable("export_manifests");

        builder.HasKey(manifest => manifest.Id);

        builder.Property(manifest => manifest.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(manifest => manifest.ExportJobId)
            .HasColumnName("export_job_id")
            .IsRequired();

        builder.Property(manifest => manifest.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(manifest => manifest.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(manifest => manifest.Scope)
            .HasColumnName("scope")
            // Persist the scope as its stable name (mirrors export_jobs.scope).
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(manifest => manifest.ManifestVersion)
            .HasColumnName("manifest_version")
            .IsRequired();

        builder.Property(manifest => manifest.GeneratedAt)
            .HasColumnName("generated_at")
            .IsRequired();

        // Workspace reads lead with the workspace column (docs/10_DATABASE_SCHEMA.md critical index
        // export_manifests(workspace_id, id)).
        builder.HasIndex(manifest => new { manifest.WorkspaceId, manifest.Id })
            .HasDatabaseName("ix_export_manifests_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization boundary check
        // (checked before the workspace boundary) and tenant-scoped reads efficient (threat T5).
        builder.HasIndex(manifest => new { manifest.OrganizationId, manifest.WorkspaceId })
            .HasDatabaseName("ix_export_manifests_organization_id_workspace_id");

        // Per-job unique natural key: exactly one manifest per export job. Backs the find-by-job lookup.
        builder.HasIndex(manifest => manifest.ExportJobId)
            .IsUnique()
            .HasDatabaseName("ix_export_manifests_export_job_id");

        // Tenant foreign key: every manifest hangs off exactly one organization (the owner of its
        // workspace). Cascade delete removes manifests with their tenant (a manifest has no meaning without
        // its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(manifest => manifest.OrganizationId)
            .HasConstraintName("fk_export_manifests_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every manifest hangs off exactly one workspace. Cascade delete removes
        // manifests with their workspace.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(manifest => manifest.WorkspaceId)
            .HasConstraintName("fk_export_manifests_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Producing-job foreign key: every manifest is produced by exactly one export job. Cascade delete
        // removes the manifest when its job is deleted (a manifest has no meaning without its job).
        builder.HasOne<ExportJob>()
            .WithMany()
            .HasForeignKey(manifest => manifest.ExportJobId)
            .HasConstraintName("fk_export_manifests_export_jobs_export_job_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Per-kind inventory: a one-to-many relationship to the child entries over the aggregate's read-only
        // Entries collection. The child's export_manifest_id foreign key cascades on delete so a manifest's
        // entries are removed with it.
        builder.HasMany(manifest => manifest.Entries)
            .WithOne()
            .HasForeignKey(entry => entry.ExportManifestId)
            .HasConstraintName("fk_export_manifest_entries_export_manifests_export_manifest_id")
            .OnDelete(DeleteBehavior.Cascade);

        // The Entries collection is exposed read-only (IReadOnlyList over a backing field), so EF reads and
        // writes it through the field rather than the property setter.
        builder.Metadata
            .FindNavigation(nameof(ExportManifest.Entries))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}
