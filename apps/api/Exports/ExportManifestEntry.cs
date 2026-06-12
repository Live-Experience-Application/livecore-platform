namespace LiveCore.Api.Exports;

/// <summary>
/// One inventory line of an <see cref="ExportManifest"/> (CORE-AUD-003): the COUNT of resources of a single
/// generic <see cref="ExportResourceKind"/> that a produced workspace export covered. An entry is part of
/// the manifest aggregate — it is never created or queried on its own, only ever through its owning
/// <see cref="ExportManifest"/> (created together by <see cref="ExportManifest.ForWorkspaceExport"/> and
/// loaded with the manifest), so it carries no tenant or workspace column of its own: it is reached only by
/// cascading from the tenant- and workspace-scoped manifest row.
///
/// NO CONTENT (threats T7/T8 in docs/07_SECURITY_THREAT_MODEL.md). An entry holds a kind and a non-negative
/// count — never any exported scene/content body. The manifest therefore reveals only the SHAPE of an
/// export (how many of each kind), never the data itself.
///
/// IMMUTABLE. A manifest is the durable, write-once record of what an export produced, so its entries never
/// change after creation: every property is get-only and there is no mutator (mirrors the append-only
/// <c>AuditLogEntry</c>).
/// </summary>
public sealed class ExportManifestEntry
{
    private ExportManifestEntry(Guid id, Guid exportManifestId, ExportResourceKind kind, int itemCount)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Export manifest entry id must not be empty.", nameof(id));
        }

        if (exportManifestId == Guid.Empty)
        {
            throw new ArgumentException("Export manifest id must not be empty.", nameof(exportManifestId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Kind is not a defined export resource kind.");
        }

        // A count is an inventory size: it may be zero (the export covered no resources of this kind) but
        // never negative.
        if (itemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "Item count must not be negative.");
        }

        Id = id;
        ExportManifestId = exportManifestId;
        Kind = kind;
        ItemCount = itemCount;
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private ExportManifestEntry()
    {
    }

    /// <summary>Surrogate key of the entry row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>
    /// The id of the <see cref="ExportManifest"/> this entry belongs to (the <c>export_manifest_id</c>
    /// foreign key into <c>export_manifests</c>, cascade on delete). Immutable; an entry never moves to
    /// another manifest.
    /// </summary>
    public Guid ExportManifestId { get; }

    /// <summary>The generic kind of resource this entry counts. Persisted by its stable name.</summary>
    public ExportResourceKind Kind { get; }

    /// <summary>How many resources of <see cref="Kind"/> the export covered (non-negative). Never content.</summary>
    public int ItemCount { get; }

    /// <summary>
    /// Creates an inventory entry for the given manifest, kind and non-negative count. Internal to the
    /// Exports module: entries are only ever built by <see cref="ExportManifest.ForWorkspaceExport"/> as
    /// part of building the manifest aggregate.
    /// </summary>
    /// <exception cref="ArgumentException">The id or manifest id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind is undefined or the count is negative.</exception>
    internal static ExportManifestEntry Create(Guid exportManifestId, ExportResourceKind kind, int itemCount)
        => new(Guid.CreateVersion7(), exportManifestId, kind, itemCount);

    /// <summary>Identifier-only representation that is safe for structured logs (threats T7/T8).</summary>
    public override string ToString()
        => $"ExportManifestEntry {Id} manifest={ExportManifestId} kind={Kind} count={ItemCount}";
}
