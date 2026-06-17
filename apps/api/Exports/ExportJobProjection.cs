// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;

namespace LiveCore.Api.Exports;

/// <summary>
/// FULL, host/metadata-facing projection of an <see cref="ExportJob"/> (CORE-AUD-002). It is the complete,
/// product-neutral, server-side view of an export job returned to the host-capable / metadata-entitled
/// workspace roles — the FULL half of the role-based export projection, the "export role-based projection"
/// control for threat T8 ("Export leak") in docs/07_SECURITY_THREAT_MODEL.md.
///
/// This is one of the TWO distinct export-job view types (docs/08_API_CONTRACTS.md DTO design rule "Host
/// DTOs and Participant DTOs are different"): the full view, while <see cref="ExportJobSummaryView"/> is
/// the host-only-field-stripped, audience-safe view. <see cref="ExportJobProjection"/> selects between
/// them from the caller's workspace role; this type is NEVER produced for an audience (Participant/Observer)
/// role.
///
/// The view is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md): identifiers, the tenant and
/// workspace boundaries, the requester, the explicit export <see cref="ExportJob.Scope"/>, the lifecycle
/// status, the optional generic failure reason and the server timestamps. It carries NO exported content
/// and NO internal authorization rationale (docs/08; threats T7/T8).
/// </summary>
/// <param name="Id">Surrogate id of the export job (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the job belongs to.</param>
/// <param name="WorkspaceId">Workspace the job belongs to.</param>
/// <param name="RequestedByUserProfileId">The user that requested the export, or <see langword="null"/> when anonymized.</param>
/// <param name="Scope">The explicit export scope the job was authorized for.</param>
/// <param name="Status">The lifecycle status of the job.</param>
/// <param name="FailureReason">The generic failure reason when the job failed, otherwise <see langword="null"/>.</param>
/// <param name="CreatedAt">When the job was requested (UTC).</param>
/// <param name="UpdatedAt">When the job's status last changed (UTC).</param>
public sealed record ExportJobView(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid? RequestedByUserProfileId,
    ExportScope Scope,
    ExportJobStatus Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects an <see cref="ExportJob"/> aggregate into its full view. Only the generic, non-content
    /// fields are copied (every field of the aggregate except the materialization plumbing); there is no
    /// exported data on the aggregate to leak.
    /// </summary>
    public static ExportJobView From(ExportJob exportJob)
    {
        ArgumentNullException.ThrowIfNull(exportJob);

        return new ExportJobView(
            exportJob.Id,
            exportJob.OrganizationId,
            exportJob.WorkspaceId,
            exportJob.RequestedByUserProfileId,
            exportJob.Scope,
            exportJob.Status,
            exportJob.FailureReason,
            exportJob.CreatedAt,
            exportJob.UpdatedAt);
    }
}

/// <summary>
/// AUDIENCE-safe, host-only-field-STRIPPED projection of an <see cref="ExportJob"/> (CORE-AUD-002). It is
/// the second of the TWO export-job view types: the shape produced for the audience workspace roles instead
/// of the full <see cref="ExportJobView"/>. docs/08_API_CONTRACTS.md states "Host DTOs and Participant DTOs
/// are different" and "Participant DTOs must not contain hidden content fields", so it is a SEPARATE record
/// type, not a reused/optional-field variant of the full view.
///
/// EXACT field set and the justification for each OMISSION (a deliberately minimal, audience-safe set that
/// honors the "export role-based projection" control for threat T8):
/// <list type="bullet">
///   <item><c>Id</c> — KEPT: the surrogate id is a non-sensitive correlation handle; it never authorizes
///   access on its own (every lookup is tenant+workspace scoped, threat T1/T5).</item>
///   <item><c>Scope</c> — KEPT: the explicit, generic export scope (Workspace/UserData) is the
///   audience-relevant "what is being exported"; it is a coarse, non-content classification, not hidden
///   data.</item>
///   <item><c>Status</c> — KEPT: the lifecycle status is the audience-relevant "where the export is up to";
///   it is a coarse state name, not content.</item>
///   <item><c>OrganizationId</c> / <c>WorkspaceId</c> — OMITTED: the internal tenant/workspace boundary
///   identifiers, stripped because docs/08 forbids hidden/internal fields in participant DTOs and they are
///   isolation boundaries (threat T5), not audience-relevant export identity.</item>
///   <item><c>RequestedByUserProfileId</c> — OMITTED: WHO requested the export is host/audit metadata, not
///   audience-relevant; revealing another user's identity to the audience is stripped (threats T7/T8).</item>
///   <item><c>FailureReason</c> — OMITTED: the generic diagnostic is host/operator metadata; the audience
///   shape exposes only the coarse <c>Status</c>, never any failure detail (threats T7/T8).</item>
///   <item><c>CreatedAt</c> / <c>UpdatedAt</c> — OMITTED: host/operator timestamps, kept out of the
///   audience shape exactly as the participant scene shape omits the host preparation timestamps.</item>
/// </list>
///
/// It carries NO host-only fields, NO exported content and NO internal authorization rationale (docs/08;
/// threats T7/T8). Adding any host-only field here would be caught by the projection tests that assert its
/// EXACT property set.
/// </summary>
/// <param name="Id">Surrogate id of the export job (UUIDv7); a non-sensitive handle.</param>
/// <param name="Scope">The explicit export scope the job was authorized for.</param>
/// <param name="Status">The lifecycle status of the job.</param>
public sealed record ExportJobSummaryView(
    Guid Id,
    ExportScope Scope,
    ExportJobStatus Status)
{
    /// <summary>
    /// Projects an <see cref="ExportJob"/> aggregate into its audience-safe summary view. Only the minimal
    /// audience-relevant fields (id, scope, status) are copied; the tenant/workspace boundary ids, the
    /// requester, the failure reason and the timestamps are deliberately NOT copied (see the type summary).
    /// </summary>
    public static ExportJobSummaryView From(ExportJob exportJob)
    {
        ArgumentNullException.ThrowIfNull(exportJob);

        return new ExportJobSummaryView(
            exportJob.Id,
            exportJob.Scope,
            exportJob.Status);
    }
}

/// <summary>
/// Role-based export-job PROJECTOR (CORE-AUD-002) — the pure mapping that honors the "export role-based
/// projection" control for threat T8 ("Export leak") in docs/07_SECURITY_THREAT_MODEL.md and the epic
/// acceptance criterion "Audit/export/recap are generic and authorized". It selects WHICH of the two
/// distinct export-job view shapes (<see cref="ExportJobView"/> full vs <see cref="ExportJobSummaryView"/>
/// audience-safe) a caller receives, from the caller's WORKSPACE <see cref="MembershipRole"/>.
///
/// This projector decides the SHAPE a viewer receives, NOT whether a viewer may access export jobs at all:
/// the HTTP route that lists export jobs and its server-side access authorization (who may even see a
/// workspace's export jobs — the "Export workspace" / "View audit log" rows of
/// docs/06_AUTHORIZATION_MATRIX.md, with their deployment-"optional" entries) is a later Exports story (the
/// export manifest/endpoint work, CORE-AUD-003+). This story models the job and its role-based view shapes;
/// the projector is the reusable, fail-closed core those later endpoints sit on.
///
/// Role -> shape mapping, decided STRICTLY from the "View workspace metadata" row of
/// docs/06_AUTHORIZATION_MATRIX.md (the same crisp partition <c>SceneProjection</c> uses for scene
/// metadata, so the view-shape decision is not reinvented): Owner/Admin/Host/CoHost = <c>yes</c> and
/// Auditor = <c>yes</c> get the full view; Participant/Observer = <c>limited</c> get the stripped summary
/// view. <see cref="MembershipRole"/> is NON-LINEAR (its integer values are storage discriminators only and
/// must never be compared with &gt;/&lt;), so the classification is an EXACT set-membership check, never an
/// ordering comparison. An undefined enum value fails closed to the stripped summary shape (deny-by-default:
/// an unrecognized role is never granted the broader full view).
/// </summary>
internal static class ExportJobProjection
{
    /// <summary>
    /// Whether the given workspace role receives the full <see cref="ExportJobView"/> rather than the
    /// stripped <see cref="ExportJobSummaryView"/>. EXACT set membership over the roles whose "View
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
    /// Projects the given export jobs into the role-appropriate view array, boxed as <see cref="object"/>
    /// so a later endpoint can return either the full (<see cref="ExportJobView"/>) or the summary
    /// (<see cref="ExportJobSummaryView"/>) array as the response body. The SET of jobs is unchanged — only
    /// the per-job SHAPE differs by role.
    /// </summary>
    public static object Project(IEnumerable<ExportJob> exportJobs, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(exportJobs);

        return ReceivesFullView(role)
            ? exportJobs.Select(ExportJobView.From).ToArray()
            : exportJobs.Select(ExportJobSummaryView.From).ToArray();
    }
}
