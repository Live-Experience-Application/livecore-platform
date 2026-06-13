namespace LiveCore.Api.Exports;

/// <summary>
/// System read that INVENTORIES a workspace for the background export processing job (CORE-JOB-002): given a
/// tenant and workspace, it returns the per-kind COUNT of the workspace's resources — how many
/// <see cref="ExportResourceKind.Session"/>s, <see cref="ExportResourceKind.Scene"/>s,
/// <see cref="ExportResourceKind.ContentBlock"/>s, <see cref="ExportResourceKind.Entity"/>s,
/// <see cref="ExportResourceKind.Participant"/>s and <see cref="ExportResourceKind.Asset"/>s it holds. The
/// processor turns this inventory into the produced workspace export <see cref="ExportManifest"/> (one
/// <see cref="ExportManifestEntry"/> per kind) through <see cref="ExportManifest.ForWorkspaceExport"/>.
///
/// <para>
/// COUNTS ONLY — NEVER CONTENT (threats T7/T8 in docs/07_SECURITY_THREAT_MODEL.md). The inventory reveals only
/// the SHAPE of a workspace export (how many of each kind), exactly as the manifest itself does; it never reads
/// a scene title, a content body, an asset's bytes or any other free-form field, so producing the manifest can
/// never leak exported content into the manifest, a log or a metric.
/// </para>
///
/// <para>
/// MODULE BOUNDARY. Inventorying the workspace's resources is intrinsic to producing a "workspace export",
/// which the Exports module owns (docs/05_MODULE_CONTRACTS.md). The reader needs the COUNTS of several other
/// modules' workspace-scoped tables (Sessions, Scenes, Content, Entities, Participants, Assets), so it is the
/// "explicit contract" cross-module READ the architecture wants (docs/02_ARCHITECTURE.md), not a table
/// ownership violation: it only ever counts rows, never writes a foreign table and never projects a foreign
/// row's content. The catalog of counted kinds matches <see cref="ExportResourceKind"/>, the primary
/// workspace-scoped content kinds a workspace export covers.
/// </para>
///
/// TENANT SAFETY (threat T5). Every count is scoped to exactly the given (organization, workspace) pair, with
/// the organization boundary checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md), so one
/// workspace's export can never count another workspace's — or another tenant's — resources. The produced
/// manifest therefore inventories only its own workspace.
/// </summary>
public interface IWorkspaceExportInventoryReader
{
    /// <summary>
    /// Counts the resources of the given workspace (owned by the given organization), grouped by generic
    /// <see cref="ExportResourceKind"/>. The returned dictionary always contains every defined kind (a kind
    /// the workspace has none of maps to <c>0</c>), so the produced manifest is a complete, deterministic
    /// inventory. Every count is tenant- AND workspace-scoped (threat T5).
    /// </summary>
    /// <param name="organizationId">The tenant whose workspace is inventoried (the boundary checked first).</param>
    /// <param name="workspaceId">The workspace to inventory.</param>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <exception cref="System.ArgumentException">The organization id or workspace id is empty.</exception>
    Task<IReadOnlyDictionary<ExportResourceKind, int>> CountWorkspaceResourcesAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);
}
