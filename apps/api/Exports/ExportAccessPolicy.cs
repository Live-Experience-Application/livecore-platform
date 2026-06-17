// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;

namespace LiveCore.Api.Exports;

/// <summary>
/// Export DOWNLOAD authorization policy (CORE-EXP-001, the "Add the export read and download endpoint" story
/// of the "Vertical Authoring and Read API Completeness" epic) — the reusable, fail-closed server-side
/// decision of WHO may retrieve a completed workspace export artifact. It is the access-gate the export
/// read/download endpoint composes on top of the tenant + workspace resolution and the existing role-based
/// <see cref="ExportManifestProjection"/>, so the export READ path is "generic and authorized" (the epic
/// acceptance criterion) and an export is delivered ONLY after a server-side permission check (threat T8
/// "Export leak" in docs/07_SECURITY_THREAT_MODEL.md).
///
/// PERMISSION — the "Export workspace" row of docs/06_AUTHORIZATION_MATRIX.md. The matrix grants the
/// workspace export to Owner = <c>yes</c>, Admin = <c>yes</c>, Host = <c>optional</c>, Auditor =
/// <c>optional</c>, and denies CoHost/Participant/Observer = <c>no</c>. The authorized download set is the
/// EXACT set {Owner, Admin, Host} — the host/admin authoring roles that own a workspace export:
/// <list type="bullet">
///   <item>Owner / Admin — the administrative roles that govern the tenant (matrix <c>yes</c>).</item>
///   <item>Host — the workspace's authoring host. The matrix marks Host as deployment-<c>optional</c>; for the
///   export ARTIFACT this story enables it (an export is host-operational data and the host who runs a session
///   is the natural retriever of its export — the required tests name "Host/Owner/Admin" as the
///   downloaders).</item>
///   <item>CoHost — denied (matrix <c>no</c>): the co-host assists with live authoring but does not own the
///   workspace export.</item>
///   <item>Auditor — denied. The Auditor is a read-only metadata role: it reads the append-only audit log
///   (<see cref="Audit.AuditQueryPolicy"/>) and receives the metadata-shaped <see cref="ExportManifestView"/>
///   through the role-based <see cref="ExportManifestProjection"/>, but the downloadable export artifact is
///   host/admin content, not an audit-metadata grant (docs/06: "Audit roles may view metadata ... unless
///   explicitly allowed" — the export artifact is not that explicit allowance). The matrix's
///   deployment-<c>optional</c> Auditor grant therefore fails CLOSED here (deny-by-default; threats
///   T1/T5/T8).</item>
/// </list>
/// The audience roles Participant/Observer and any UNDEFINED enum value are denied too.
/// <see cref="MembershipRole"/> is NON-LINEAR (its integer values are storage discriminators only and must
/// never be compared with &gt;/&lt;), so the grant is an EXACT set-membership check, never an ordering
/// comparison.
///
/// SCOPE. This policy decides WHETHER a caller may download an export; it does NOT itself enforce the tenant
/// boundary. The export job and its manifest are always loaded through the tenant- and workspace-scoped
/// <see cref="IExportJobRepository"/> / <see cref="IExportManifestRepository"/>, so one workspace's export is
/// never reachable through another workspace's or another tenant's id (threats T5/T1). The endpoint composes
/// the layers — resolve the trusted tenant, load the job within it, resolve the caller's role in the job's OWN
/// workspace, gate on <see cref="CanDownloadExport"/> (a denied known member is a 403; a non-member or foreign
/// tenant is hidden as 404), and only THEN project and return the artifact — exactly as the asset
/// signed-download flow authorizes before minting any URL.
/// </summary>
internal static class ExportAccessPolicy
{
    /// <summary>
    /// Whether the given workspace role may DOWNLOAD a completed workspace export artifact — the "Export
    /// workspace" permission of docs/06_AUTHORIZATION_MATRIX.md. EXACT set membership over the authorized set
    /// {Owner, Admin, Host}; CoHost (matrix <c>no</c>), Auditor (the deployment-<c>optional</c> metadata role),
    /// the audience roles Participant/Observer, and any undefined value all return <see langword="false"/>
    /// (deny-by-default, fail-closed; threats T1/T5/T8). Never a &gt;/&lt; comparison.
    /// </summary>
    public static bool CanDownloadExport(MembershipRole role)
        => role is MembershipRole.Owner
            or MembershipRole.Admin
            or MembershipRole.Host;
}
