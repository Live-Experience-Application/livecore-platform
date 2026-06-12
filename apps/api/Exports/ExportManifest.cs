namespace LiveCore.Api.Exports;

/// <summary>
/// Workspace export MANIFEST aggregate of the Exports module (CORE-AUD-003, the workspace export manifest
/// story of the "Audit, Export and Recap" epic).
///
/// An <see cref="ExportManifest"/> is the Core-owned, product-neutral TABLE OF CONTENTS of a produced
/// workspace export (docs/05_MODULE_CONTRACTS.md: the Exports module owns "export manifests" and "workspace
/// export"). It is the durable record of WHAT a completed <see cref="ExportJob"/> covered: a per-kind
/// inventory (<see cref="Entries"/>) of how many resources of each generic <see cref="ExportResourceKind"/>
/// the export included. CORE-AUD-002 modeled the export JOB and deliberately left "the produced export
/// manifest" to this story; this aggregate is that manifest. It stores no vertical product language — a
/// generic workspace export only (docs/04_PRODUCT_BOUNDARIES.md, csv/forbidden_core_terms.csv).
///
/// GENERIC AND AUTHORIZED — the epic acceptance criterion ("Audit/export/recap are generic and
/// authorized"). The manifest is authorized along the same structural axes as the job it belongs to, all
/// immutable for its lifetime:
/// <list type="bullet">
///   <item>TENANT/WORKSPACE SCOPE: the manifest is workspace-scoped and tenant-scoped, carrying both
///   <see cref="OrganizationId"/> (the tenant; checked before the workspace boundary) and
///   <see cref="WorkspaceId"/>, mirroring <c>ExportJob</c>. The surrogate <see cref="Id"/> alone never
///   authorizes access: every lookup is scoped by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/>
///   so one workspace's manifest can never be reached through another workspace's or another tenant's id
///   (threat T5 tenant isolation; threat T1 broken object-level authorization).</item>
///   <item>EXPLICIT EXPORT SCOPE: the manifest carries the explicit <see cref="ExportScope"/> of the job
///   that produced it — the "explicit host/admin export scopes" control for threat T8 ("Export leak"). The
///   workspace export manifest factory pins it to <see cref="ExportScope.Workspace"/>; the scope is recorded
///   from the job and can never be widened afterwards.</item>
/// </list>
///
/// ROLE-BASED PROJECTION (threat T8 "export role-based projection"). The per-kind inventory reveals the
/// shape of a workspace's host content, so it is host/metadata-only: <see cref="ExportManifestProjection"/>
/// returns the full <see cref="ExportManifestView"/> (with the entries) to host/metadata roles and the
/// stripped, audience-safe <see cref="ExportManifestSummaryView"/> to audience roles, fail-closed to the
/// summary shape for any undefined role.
///
/// NO SENSITIVE CONTENT (threats T7/T8). The aggregate holds identifiers, an enum scope, a version, a
/// timestamp and per-kind COUNTS only — never the exported data and never any scene/content body.
/// <see cref="ToString"/> is identifier-only.
///
/// IMMUTABLE / WRITE-ONCE. A manifest is the produced output of a completed export, so it is never mutated
/// after creation: every property is get-only, the entry collection is read-only, and there is no
/// transition method, setter or update path (mirrors the append-only <c>AuditLogEntry</c>). The worker that
/// drives the export and produces the manifest, and any export-request/list HTTP endpoint with its
/// server-side access authorization, are later Exports stories (exactly as CORE-AUD-002 deferred the export
/// endpoint); this story models the manifest, its persistence, its EF migration and its role-based
/// projection.
/// </summary>
public sealed class ExportManifest
{
    /// <summary>
    /// The current manifest FORMAT version. The manifest is a produced, versioned Core artifact
    /// (docs/02_ARCHITECTURE.md: "every public contract is versioned"); a future change to the manifest
    /// shape bumps this so a consumer can tell which layout it is reading.
    /// </summary>
    public const int CurrentManifestVersion = 1;

    private readonly List<ExportManifestEntry> _entries = [];

    private ExportManifest(
        Guid id,
        Guid exportJobId,
        Guid organizationId,
        Guid workspaceId,
        ExportScope scope,
        int manifestVersion,
        DateTimeOffset generatedAt,
        IEnumerable<ExportManifestEntry> entries)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Export manifest id must not be empty.", nameof(id));
        }

        if (exportJobId == Guid.Empty)
        {
            throw new ArgumentException("Export job id must not be empty.", nameof(exportJobId));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Scope is not a defined export scope.");
        }

        if (manifestVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(manifestVersion),
                manifestVersion,
                "Manifest version must be positive.");
        }

        Id = id;
        ExportJobId = exportJobId;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        Scope = scope;
        ManifestVersion = manifestVersion;
        // Normalized to UTC so the persisted timestamptz value is offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        GeneratedAt = generatedAt.ToUniversalTime();
        _entries.AddRange(entries);
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private ExportManifest()
    {
    }

    /// <summary>
    /// Surrogate key of the manifest row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md). On
    /// its own it never authorizes access: lookups are always scoped by <see cref="OrganizationId"/> and
    /// <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// The id of the <see cref="ExportJob"/> that produced this manifest (the <c>export_job_id</c> foreign
    /// key into <c>export_jobs</c>, cascade on delete, UNIQUE — one manifest per job). Immutable.
    /// </summary>
    public Guid ExportJobId { get; }

    /// <summary>
    /// Tenant boundary of the manifest: the id of the organization that owns the workspace this manifest
    /// belongs to (the <c>organization_id</c> foreign key into <c>organizations</c>). Immutable; the
    /// organization boundary is checked before the workspace boundary (threat T5).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the manifest: the id of the workspace this manifest belongs to (the
    /// <c>workspace_id</c> foreign key into <c>workspaces</c>). Immutable (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// The explicit, generic <see cref="ExportScope"/> of the export that produced this manifest. The
    /// workspace export manifest is always <see cref="ExportScope.Workspace"/>. Immutable (threat T8
    /// "explicit host/admin export scopes").
    /// </summary>
    public ExportScope Scope { get; }

    /// <summary>The manifest FORMAT version (see <see cref="CurrentManifestVersion"/>). Immutable.</summary>
    public int ManifestVersion { get; }

    /// <summary>When this manifest was produced (UTC).</summary>
    public DateTimeOffset GeneratedAt { get; }

    /// <summary>
    /// The per-kind inventory of the export — how many resources of each generic
    /// <see cref="ExportResourceKind"/> the export covered. Read-only: a manifest is write-once (see the
    /// type summary). Carries COUNTS only, never any exported content (threats T7/T8).
    /// </summary>
    public IReadOnlyList<ExportManifestEntry> Entries => _entries;

    /// <summary>
    /// The total number of resources the export covered across all kinds (the sum of the entry counts). A
    /// coarse, non-content size of the export.
    /// </summary>
    public int TotalItemCount => _entries.Sum(entry => entry.ItemCount);

    /// <summary>
    /// Produces the workspace export manifest for a COMPLETED workspace-scoped <see cref="ExportJob"/> from
    /// the given per-kind inventory. The manifest records the job it was produced for, the job's tenant,
    /// workspace and (workspace) scope, the current manifest format version and one
    /// <see cref="ExportManifestEntry"/> per inventory kind, in a deterministic order (ascending kind). The
    /// caller (a later export worker) supplies the already-resolved, completed job and the counts it
    /// produced; the counts are the only export-shaped data recorded — never any exported content
    /// (threats T7/T8).
    /// </summary>
    /// <param name="exportJob">The completed, workspace-scoped export job whose output this manifest describes.</param>
    /// <param name="inventory">
    /// The per-kind resource counts the export covered. Each key must be a defined
    /// <see cref="ExportResourceKind"/> and each value a non-negative count; a kind never appears twice (the
    /// dictionary key enforces it). May be empty (an export that covered nothing).
    /// </param>
    /// <param name="generatedAt">When the manifest was produced.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exportJob"/> or <paramref name="inventory"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The job is not <see cref="ExportScope.Workspace"/>-scoped, the job is not
    /// <see cref="ExportJobStatus.Completed"/>, or an inventory count is negative.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An inventory kind is not a defined <see cref="ExportResourceKind"/>.</exception>
    public static ExportManifest ForWorkspaceExport(
        ExportJob exportJob,
        IReadOnlyDictionary<ExportResourceKind, int> inventory,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(exportJob);
        ArgumentNullException.ThrowIfNull(inventory);

        // A WORKSPACE export manifest describes a workspace-scoped export only. A user-data export's manifest
        // is a separate, narrower artifact (a later story); this factory never widens a user-data export into
        // a workspace one (threat T8 "explicit host/admin export scopes").
        if (exportJob.Scope != ExportScope.Workspace)
        {
            throw new ArgumentException(
                "A workspace export manifest can only be produced for a workspace-scoped export job.",
                nameof(exportJob));
        }

        // A manifest is the OUTPUT of a finished export: it is produced only once the export has completed
        // successfully, so a still-pending, running or failed job has no manifest to describe.
        if (exportJob.Status != ExportJobStatus.Completed)
        {
            throw new ArgumentException(
                "A workspace export manifest can only be produced for a completed export job.",
                nameof(exportJob));
        }

        var manifestId = Guid.CreateVersion7();

        // Build the entries in a deterministic ascending-kind order so the persisted inventory and every
        // projection are stable and repeatable. Each kind is validated in ExportManifestEntry.Create.
        var entries = inventory
            .OrderBy(pair => pair.Key)
            .Select(pair => ExportManifestEntry.Create(manifestId, pair.Key, pair.Value))
            .ToList();

        return new ExportManifest(
            manifestId,
            exportJob.Id,
            exportJob.OrganizationId,
            exportJob.WorkspaceId,
            exportJob.Scope,
            CurrentManifestVersion,
            generatedAt,
            entries);
    }

    /// <summary>
    /// Whether this manifest belongs to exactly the given organization. Empty ids match nothing. The
    /// tenant-boundary check, checked before the workspace boundary (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this manifest belongs to exactly the given workspace. Empty ids match nothing (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this manifest is the one identified by the given id WITHIN the given organization and
    /// workspace. All three must match; an empty id matches nothing. The surrogate id is only ever honored
    /// inside its tenant and workspace (threats T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: manifest id, the job it was produced
    /// for, organization id, workspace id, scope, version, entry count and total item count. It carries no
    /// exported content — only identifiers, an enum, a version and coarse counts (threats T7/T8 in
    /// docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not content).
    /// </summary>
    public override string ToString()
        => $"ExportManifest {Id} job={ExportJobId} org={OrganizationId} ws={WorkspaceId} "
            + $"scope={Scope} v={ManifestVersion} kinds={_entries.Count} total={TotalItemCount}";
}
