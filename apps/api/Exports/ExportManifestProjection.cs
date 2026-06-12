using LiveCore.Api.Organizations;

namespace LiveCore.Api.Exports;

/// <summary>
/// One inventory line of the FULL export-manifest view (CORE-AUD-003): the generic resource
/// <see cref="Kind"/> and the non-negative <see cref="ItemCount"/> of resources of that kind the export
/// covered. Projected from an <see cref="ExportManifestEntry"/>. Carries a count only — never any exported
/// content (threats T7/T8 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Kind">The generic kind of resource counted.</param>
/// <param name="ItemCount">How many resources of <paramref name="Kind"/> the export covered.</param>
public sealed record ExportManifestEntryView(ExportResourceKind Kind, int ItemCount)
{
    /// <summary>Projects an <see cref="ExportManifestEntry"/> into its view line.</summary>
    public static ExportManifestEntryView From(ExportManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ExportManifestEntryView(entry.Kind, entry.ItemCount);
    }
}

/// <summary>
/// FULL, host/metadata-facing projection of an <see cref="ExportManifest"/> (CORE-AUD-003). It is the
/// complete, product-neutral, server-side view of a workspace export manifest returned to the
/// host-capable / metadata-entitled workspace roles — the FULL half of the role-based export projection,
/// the "export role-based projection" control for threat T8 ("Export leak") in
/// docs/07_SECURITY_THREAT_MODEL.md.
///
/// This is one of the TWO distinct manifest view types (docs/08_API_CONTRACTS.md DTO design rule "Host DTOs
/// and Participant DTOs are different"): the full view, while <see cref="ExportManifestSummaryView"/> is the
/// host-only-field-stripped, audience-safe view. <see cref="ExportManifestProjection"/> selects between them
/// from the caller's workspace role; this type is NEVER produced for an audience (Participant/Observer)
/// role, because its per-kind <see cref="Entries"/> inventory reveals the shape of the workspace's host
/// content.
///
/// The view is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md): identifiers, the produced job,
/// the tenant and workspace boundaries, the explicit export <see cref="Scope"/>, the manifest format
/// version, the produced timestamp, the per-kind inventory and its total. It carries NO exported content and
/// NO internal authorization rationale (docs/08; threats T7/T8).
/// </summary>
/// <param name="Id">Surrogate id of the manifest (UUIDv7).</param>
/// <param name="ExportJobId">The export job that produced this manifest.</param>
/// <param name="OrganizationId">Tenant the manifest belongs to.</param>
/// <param name="WorkspaceId">Workspace the manifest belongs to.</param>
/// <param name="Scope">The explicit export scope of the producing job (Workspace).</param>
/// <param name="ManifestVersion">The manifest format version.</param>
/// <param name="GeneratedAt">When the manifest was produced (UTC).</param>
/// <param name="Entries">The per-kind inventory, in ascending-kind order.</param>
/// <param name="TotalItemCount">The total number of resources the export covered across all kinds.</param>
public sealed record ExportManifestView(
    Guid Id,
    Guid ExportJobId,
    Guid OrganizationId,
    Guid WorkspaceId,
    ExportScope Scope,
    int ManifestVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ExportManifestEntryView> Entries,
    int TotalItemCount)
{
    /// <summary>
    /// Projects an <see cref="ExportManifest"/> aggregate into its full view. The inventory is projected in
    /// ascending-kind order so the view is deterministic regardless of load order; there is no exported data
    /// on the aggregate to leak.
    /// </summary>
    public static ExportManifestView From(ExportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var entries = manifest.Entries
            .OrderBy(entry => entry.Kind)
            .Select(ExportManifestEntryView.From)
            .ToArray();

        return new ExportManifestView(
            manifest.Id,
            manifest.ExportJobId,
            manifest.OrganizationId,
            manifest.WorkspaceId,
            manifest.Scope,
            manifest.ManifestVersion,
            manifest.GeneratedAt,
            entries,
            manifest.TotalItemCount);
    }
}

/// <summary>
/// AUDIENCE-safe, host-only-field-STRIPPED projection of an <see cref="ExportManifest"/> (CORE-AUD-003). It
/// is the second of the TWO manifest view types: the shape produced for the audience workspace roles instead
/// of the full <see cref="ExportManifestView"/>. docs/08_API_CONTRACTS.md states "Host DTOs and Participant
/// DTOs are different" and "Participant DTOs must not contain hidden content fields", so it is a SEPARATE
/// record type, not a reused/optional-field variant of the full view.
///
/// EXACT field set and the justification for each OMISSION (a deliberately minimal, audience-safe set that
/// honors the "export role-based projection" control for threat T8):
/// <list type="bullet">
///   <item><c>Id</c> — KEPT: the surrogate id is a non-sensitive correlation handle; it never authorizes
///   access on its own (every lookup is tenant+workspace scoped, threat T1/T5).</item>
///   <item><c>Scope</c> — KEPT: the explicit, generic export scope (Workspace) is the audience-relevant
///   "what kind of export this is"; it is a coarse, non-content classification.</item>
///   <item><c>Entries</c> / <c>TotalItemCount</c> — OMITTED: the per-kind inventory and the export size
///   reveal the shape and volume of the workspace's host content, so they are host/metadata-only (threat T8
///   "export role-based projection").</item>
///   <item><c>ExportJobId</c> — OMITTED: the producing job is host/operator metadata, not audience-relevant.</item>
///   <item><c>OrganizationId</c> / <c>WorkspaceId</c> — OMITTED: the internal tenant/workspace boundary
///   identifiers, stripped because docs/08 forbids hidden/internal fields in participant DTOs and they are
///   isolation boundaries (threat T5).</item>
///   <item><c>ManifestVersion</c> / <c>GeneratedAt</c> — OMITTED: host/operator metadata, kept out of the
///   audience shape exactly as the audience scene shape omits the host preparation timestamps.</item>
/// </list>
///
/// It carries NO host-only fields, NO exported content and NO internal authorization rationale (docs/08;
/// threats T7/T8). Adding any host-only field here would be caught by the projection tests that assert its
/// EXACT property set.
/// </summary>
/// <param name="Id">Surrogate id of the manifest (UUIDv7); a non-sensitive handle.</param>
/// <param name="Scope">The explicit export scope of the producing job.</param>
public sealed record ExportManifestSummaryView(Guid Id, ExportScope Scope)
{
    /// <summary>
    /// Projects an <see cref="ExportManifest"/> aggregate into its audience-safe summary view. Only the
    /// minimal audience-relevant fields (id, scope) are copied; the producing job, the tenant/workspace
    /// boundary ids, the format version, the produced timestamp and the entire per-kind inventory are
    /// deliberately NOT copied (see the type summary).
    /// </summary>
    public static ExportManifestSummaryView From(ExportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new ExportManifestSummaryView(manifest.Id, manifest.Scope);
    }
}

/// <summary>
/// Role-based export-manifest PROJECTOR (CORE-AUD-003) — the pure mapping that honors the "export role-based
/// projection" control for threat T8 ("Export leak") in docs/07_SECURITY_THREAT_MODEL.md and the epic
/// acceptance criterion "Audit/export/recap are generic and authorized". It selects WHICH of the two
/// distinct manifest view shapes (<see cref="ExportManifestView"/> full vs
/// <see cref="ExportManifestSummaryView"/> audience-safe) a caller receives, from the caller's WORKSPACE
/// <see cref="MembershipRole"/>.
///
/// This projector decides the SHAPE a viewer receives, NOT whether a viewer may access manifests at all: the
/// HTTP route that returns a workspace export manifest and its server-side access authorization is a later
/// Exports story (the export endpoint work), exactly as CORE-AUD-002's <c>ExportJobProjection</c> left the
/// export endpoint to a later story. This story models the manifest and its role-based view shapes; the
/// projector is the reusable, fail-closed core those later endpoints sit on.
///
/// Role -> shape mapping, decided STRICTLY from the "View workspace metadata" row of
/// docs/06_AUTHORIZATION_MATRIX.md — the SAME crisp partition <c>ExportJobProjection</c> uses, so the
/// view-shape decision is not reinvented: Owner/Admin/Host/CoHost = <c>yes</c> and Auditor = <c>yes</c> get
/// the full view; Participant/Observer = <c>limited</c> get the stripped summary view.
/// <see cref="MembershipRole"/> is NON-LINEAR (its integer values are storage discriminators only and must
/// never be compared with &gt;/&lt;), so the classification is an EXACT set-membership check, never an
/// ordering comparison. An undefined enum value fails closed to the stripped summary shape (deny-by-default:
/// an unrecognized role is never granted the broader full view).
/// </summary>
internal static class ExportManifestProjection
{
    /// <summary>
    /// Whether the given workspace role receives the full <see cref="ExportManifestView"/> rather than the
    /// stripped <see cref="ExportManifestSummaryView"/>. EXACT set membership over the roles whose "View
    /// workspace metadata" is <c>yes</c> in docs/06: Owner, Admin, Host, CoHost and Auditor. Any other role
    /// (the audience roles Participant/Observer, whose metadata access is <c>limited</c>, and any undefined
    /// value) is denied the full view and falls closed to the summary view. Never a &gt;/&lt; comparison.
    /// </summary>
    public static bool ReceivesFullView(MembershipRole role)
        => role is MembershipRole.Owner
            or MembershipRole.Admin
            or MembershipRole.Host
            or MembershipRole.CoHost
            or MembershipRole.Auditor;

    /// <summary>
    /// Projects the given manifest into the role-appropriate view, boxed as <see cref="object"/> so a later
    /// endpoint can return either the full (<see cref="ExportManifestView"/>) or the summary
    /// (<see cref="ExportManifestSummaryView"/>) shape as the response body.
    /// </summary>
    public static object Project(ExportManifest manifest, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return ReceivesFullView(role)
            ? ExportManifestView.From(manifest)
            : ExportManifestSummaryView.From(manifest);
    }
}
