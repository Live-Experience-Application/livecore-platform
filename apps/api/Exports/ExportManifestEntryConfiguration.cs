using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Exports;

/// <summary>
/// Relational mapping of <see cref="ExportManifestEntry"/> to the <c>export_manifest_entries</c> table
/// (CORE-AUD-003) — the per-kind inventory rows of an <see cref="ExportManifest"/>. An entry is a CHILD of
/// the manifest aggregate: it is reached only by cascading from the tenant- and workspace-scoped
/// <c>export_manifests</c> row, never queried on its own, so it carries no <c>organization_id</c> or
/// <c>workspace_id</c> column of its own (the manifest already pins both, and an entry can never be
/// addressed outside its manifest). The parent relationship, its <c>export_manifest_id</c> foreign key and
/// the cascade-on-delete are configured on the manifest side in
/// <see cref="ExportManifestConfiguration"/>.
///
/// The <c>kind</c> column stores the enum NAME (Session/Scene/ContentBlock/Entity/Participant/Asset),
/// persisted via <c>HasConversion&lt;string&gt;</c> exactly like <c>export_jobs.scope</c>/<c>status</c>, so
/// the stored value stays readable and is not coupled to the declaration-order integers. The
/// <c>item_count</c> is a non-negative inventory size, never any exported content (threats T7/T8 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// A UNIQUE index on (<c>export_manifest_id</c>, <c>kind</c>) is the per-manifest natural key: a kind appears
/// at most once in a manifest's inventory, so a manifest can never carry two conflicting counts for the same
/// kind.
/// </summary>
internal sealed class ExportManifestEntryConfiguration : IEntityTypeConfiguration<ExportManifestEntry>
{
    public void Configure(EntityTypeBuilder<ExportManifestEntry> builder)
    {
        builder.ToTable("export_manifest_entries");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entry => entry.ExportManifestId)
            .HasColumnName("export_manifest_id")
            .IsRequired();

        builder.Property(entry => entry.Kind)
            .HasColumnName("kind")
            // Persist the kind as its stable name (not its numeric value) so the stored value stays readable
            // and is not coupled to the declaration-order integers (mirrors export_jobs.scope).
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entry => entry.ItemCount)
            .HasColumnName("item_count")
            .IsRequired();

        // Per-manifest unique natural key: a kind appears at most once in a manifest's inventory, so the
        // same kind can never be counted twice for one manifest.
        builder.HasIndex(entry => new { entry.ExportManifestId, entry.Kind })
            .IsUnique()
            .HasDatabaseName("ix_export_manifest_entries_export_manifest_id_kind");
    }
}
