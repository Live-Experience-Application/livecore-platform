// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Assets;

/// <summary>
/// Relational mapping of <see cref="Asset"/> to the <c>assets</c> table (CORE-AST-001; the Assets
/// module's first table; csv/database_tables.csv: module Assets, scope <c>workspace</c>, "Metadata
/// only"). The asset is workspace-scoped, so it carries a <c>workspace_id</c> column that is a foreign
/// key into <c>workspaces(id)</c>, the documented <c>assets(workspace_id, id)</c> shape
/// (docs/10_DATABASE_SCHEMA.md critical indexes). It is also tenant-scoped, so it carries an
/// <c>organization_id</c> column that is a foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped tables include <c>organization_id</c>"; threat
/// T5 in docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already pins the organization, but
/// storing the tenant on the row lets isolation be enforced at the row level and lets the organization
/// boundary be checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md authorization
/// principles).
///
/// METADATA ONLY (csv/database_tables.csv). The columns are the asset metadata of
/// docs/12_STORAGE_ASSETS.md (<c>storage_provider</c>, <c>bucket</c>, <c>object_key</c>,
/// <c>content_type</c>, <c>size_bytes</c>, <c>checksum</c>, <c>created_by</c>, <c>created_at</c>,
/// <c>status</c>) plus the tenant/workspace boundary and an <c>updated_at</c> audit column. The binary
/// content is NEVER stored here: it lives in S3-compatible object storage (docs/12_STORAGE_ASSETS.md;
/// "Avoid storing large binary files in PostgreSQL"). The storage coordinates address a PRIVATE bucket
/// reached only through an authorized signed URL (threat T4); no column can make the asset public.
///
/// The optional <c>created_by</c> column is a NULLABLE foreign key into <c>users(id)</c>: an asset is
/// always created by an authenticated user, but the link sets null when that user is deleted so the
/// asset survives and is anonymized rather than cascade-deleted (mirrors <c>participants.user_id</c>).
/// The nullable <c>size_bytes</c>/<c>checksum</c> columns are null while the asset is pending (the
/// upload is not yet confirmed) and stamped once the upload is confirmed.
///
/// Two indexes back the documented access patterns:
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>id</c>) is the documented critical index
///   <c>assets(workspace_id, id)</c>: it keeps workspace-scoped lookups by id efficient and makes every
///   asset read lead with the workspace column (docs/10_DATABASE_SCHEMA.md critical indexes).
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the tenant
///   boundary check (checked before the workspace boundary) and the organization foreign-key check
///   efficient and makes tenant-scoped reads lead with <c>organization_id</c> (threat T5).
///   </item>
/// </list>
/// There is deliberately NO unique natural-key index in this story: an asset is identified only by its
/// surrogate id. A storage-object-key uniqueness guarantee belongs to the upload-intent flow that mints
/// object keys (CORE-AST-003).
///
/// The tenant and workspace foreign keys cascade on delete (an asset metadata row has no meaning without
/// its tenant or workspace, so removing either removes its assets — mirrors <c>sessions</c>/<c>entities</c>);
/// the <c>created_by</c> user foreign key sets null on delete (history of an asset is retained when its
/// creator is removed — mirrors <c>participants.user_id</c>). The actual stored object's lifecycle (the
/// cleanup job that removes orphaned objects) is CORE-AST-006.
///
/// The <c>status</c> column stores the lifecycle status NAME (Pending/Available), persisted via
/// <c>HasConversion&lt;string&gt;</c> exactly like <c>sessions.status</c> and <c>participants.status</c>,
/// so the stored value stays readable and is not coupled to the declaration-order integers
/// (docs/10_DATABASE_SCHEMA.md soft-delete/state principle).
///
/// Deployment requirement: equality on the id columns is binary, so no collation concern applies.
/// Pinning any column collation, if ever needed, is planned with the first story that runs migrations
/// against real PostgreSQL, exactly as noted for the <c>sessions</c>, <c>participants</c> and
/// <c>entities</c> tables.
/// </summary>
internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        // Defence-in-depth CHECK constraints (CORE-CONC-009), each mirroring an invariant the aggregate
        // already enforces in memory:
        //  - the status column stores the AssetStatus enum by its stable NAME (Pending -> Available), so the
        //    allowlist rejects any other value;
        //  - the size byte counter is a NON-negative count, null while the asset is pending and stamped on
        //    confirmation, so the constraint allows null OR a non-negative value.
        // The constraints reject a direct DB write, a future raw-SQL path or a mapping regression that would
        // otherwise persist a code-impossible state. Additive and behaviour-neutral.
        builder.ToTable(
            "assets",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_assets_status",
                    "status IN ('Pending', 'Available')");
                table.HasCheckConstraint(
                    "ck_assets_size_bytes",
                    "size_bytes IS NULL OR size_bytes >= 0");
            });

        builder.HasKey(asset => asset.Id);

        builder.Property(asset => asset.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(asset => asset.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(asset => asset.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(asset => asset.CreatedByUserProfileId)
            .HasColumnName("created_by")
            .IsRequired(false);

        builder.Property(asset => asset.StorageProvider)
            .HasColumnName("storage_provider")
            .HasMaxLength(Asset.MaxStorageProviderLength)
            .IsRequired();

        builder.Property(asset => asset.Bucket)
            .HasColumnName("bucket")
            .HasMaxLength(Asset.MaxBucketLength)
            .IsRequired();

        builder.Property(asset => asset.ObjectKey)
            .HasColumnName("object_key")
            .HasMaxLength(Asset.MaxObjectKeyLength)
            .IsRequired();

        builder.Property(asset => asset.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(Asset.MaxContentTypeLength)
            .IsRequired();

        // Null while the asset is pending (the upload is not yet confirmed), stamped on confirmation.
        builder.Property(asset => asset.SizeBytes)
            .HasColumnName("size_bytes")
            .IsRequired(false);

        builder.Property(asset => asset.Checksum)
            .HasColumnName("checksum")
            .HasMaxLength(Asset.MaxChecksumLength)
            .IsRequired(false);

        builder.Property(asset => asset.Status)
            .HasColumnName("status")
            // Persist the status as its stable name (not its numeric value) so the stored lifecycle value
            // stays readable and is not coupled to the declaration-order integers, which exist only as
            // in-memory storage discriminators.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(asset => asset.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(asset => asset.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Documented critical index assets(workspace_id, id): workspace reads lead with the workspace
        // column (docs/10_DATABASE_SCHEMA.md critical indexes).
        builder.HasIndex(asset => new { asset.WorkspaceId, asset.Id })
            .HasDatabaseName("ix_assets_workspace_id_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization boundary
        // check (checked before the workspace boundary) and tenant-scoped reads efficient
        // (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(asset => new { asset.OrganizationId, asset.WorkspaceId })
            .HasDatabaseName("ix_assets_organization_id_workspace_id");

        // Tenant foreign key: every asset hangs off exactly one organization (the owner of its
        // workspace). Cascade delete removes assets with their tenant (a metadata row has no meaning
        // without its tenant).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(asset => asset.OrganizationId)
            .HasConstraintName("fk_assets_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every asset hangs off exactly one workspace. Cascade delete removes
        // assets with their workspace (a metadata row has no meaning without its workspace).
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(asset => asset.WorkspaceId)
            .HasConstraintName("fk_assets_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Optional creator foreign key: an asset may reference at most one user profile as its creator.
        // Set null on delete so removing the user anonymizes the asset record rather than deleting the
        // asset (other content may link to it; mirrors participants.user_id).
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(asset => asset.CreatedByUserProfileId)
            .HasConstraintName("fk_assets_users_created_by")
            .OnDelete(DeleteBehavior.SetNull);
    }
}
