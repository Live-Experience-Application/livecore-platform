using LiveCore.Api.Organizations;

namespace LiveCore.Api.Audit;

/// <summary>
/// Audit query AUTHORIZATION policy (CORE-AUD-005, the "Implement audit query permissions" story of the
/// Audit, Export and Recap epic) — the reusable, fail-closed server-side decision of WHO may read the
/// append-only audit log, and the projection of the log into the safe <see cref="AuditLogEntryView"/> view
/// it hands an authorized reader. It is what makes the audit READ path "generic and authorized" (the epic
/// acceptance criterion), the read-side counterpart to the inherently-authorized audit WRITE path (every
/// audit entry is written only as a side effect of an already-authorized command, CORE-AUD-001).
///
/// PERMISSION — the "View audit log" row of docs/06_AUTHORIZATION_MATRIX.md. The matrix grants the audit
/// read to Owner = <c>yes</c>, Admin = <c>yes</c>, Auditor = <c>yes</c>, Host = <c>optional</c>, and denies
/// CoHost/Participant/Observer = <c>no</c>. Core's secure default is the EXACT set {Owner, Admin, Auditor}:
/// <list type="bullet">
///   <item>Owner / Admin — the administrative roles that govern the tenant.</item>
///   <item>Auditor — the dedicated audit role. The audit log is the one place the matrix grants Auditor a
///   first-class <c>yes</c> (it is otherwise only <c>audit-only</c> on the content rows and never a live
///   content grant, so unlike the content/asset policies — <see cref="Visibility.VisibilityRoles"/>,
///   <see cref="Assets.AssetDownloadPolicy"/> — that DENY the auditor, this policy is exactly where the
///   auditor is meant to be allowed). docs/06: "Audit roles may view metadata ... unless explicitly allowed".
///   This is that explicit allowance.</item>
///   <item>Host — the matrix marks Host as <c>optional</c>, a deployment-configurable grant. Core fails
///   CLOSED on "optional": the secure default DENIES Host, and enabling it is a deployment policy decision
///   layered on top, deliberately not enabled in Core (deny-by-default; threats T1/T5 in
///   docs/07_SECURITY_THREAT_MODEL.md). The README documents the authorized callers as "Owner/Admin/Auditor"
///   for exactly this reason.</item>
/// </list>
/// Every other role — CoHost, the audience roles Participant/Observer, and any UNDEFINED enum value — is
/// denied. <see cref="MembershipRole"/> is NON-LINEAR (its integer values are storage discriminators only
/// and must never be compared with &gt;/&lt;), so the grant is an EXACT set-membership check, never an
/// ordering comparison.
///
/// SCOPE. This policy decides WHETHER a caller may read the audit log; it does NOT itself enforce the tenant
/// boundary. The audit log is tenant-scoped and is only ever loaded through
/// <see cref="IAuditLogRepository.ListByOrganizationAsync"/>, which filters by <c>organization_id</c> so one
/// tenant's records are never returned through another tenant's id (threat T5). A future audit query
/// endpoint composes the two layers — resolve the trusted tenant + the caller's role, gate on
/// <see cref="CanViewAuditLog"/>, then read tenant-scoped entries and <see cref="Project"/> them — exactly
/// as the export/recap projectors (<see cref="Exports.ExportJobProjection"/>,
/// <see cref="Recaps.RecapProjection"/>) are the reusable, fail-closed core their later endpoints sit on.
/// There is no audit HTTP route in csv/api_routes.csv, so none is added here.
/// </summary>
internal static class AuditQueryPolicy
{
    /// <summary>
    /// Whether the given workspace/organization role may READ the append-only audit log — the "View audit
    /// log" permission of docs/06_AUTHORIZATION_MATRIX.md. EXACT set membership over Core's secure default
    /// authorized set {Owner, Admin, Auditor}; Host (the matrix's deployment-<c>optional</c> grant), CoHost,
    /// the audience roles Participant/Observer, and any undefined value all return <see langword="false"/>
    /// (deny-by-default, fail-closed; threats T1/T5). Never a &gt;/&lt; comparison.
    /// </summary>
    public static bool CanViewAuditLog(MembershipRole role)
        => role is MembershipRole.Owner
            or MembershipRole.Admin
            or MembershipRole.Auditor;

    /// <summary>
    /// Projects the given tenant-scoped audit entries into the role-appropriate result: the safe
    /// <see cref="AuditLogEntryView"/> list for a role authorized by <see cref="CanViewAuditLog"/>, and an
    /// EMPTY list for any unauthorized role. The audit log has a single (full) shape, so this is an
    /// all-or-nothing projection — an unauthorized role is never handed a reduced audit record, it is handed
    /// nothing.
    ///
    /// The empty result is a fail-closed DEFENCE-IN-DEPTH backstop, not the primary gate: a consuming
    /// endpoint must still deny an unauthorized caller outright (it is the "View audit log" permission, so a
    /// denied known member is a 403 and a non-member/foreign tenant is hidden as 404) rather than returning
    /// this empty list as if it were a valid, empty audit log. <see cref="CanViewAuditLog"/> is that primary
    /// gate; this method guarantees that even a mis-wired caller can never read another role's audit facts.
    /// The entries are projected in the order supplied (the repository returns them in the deterministic,
    /// time-ordered surrogate-id order of the critical index <c>audit_logs(organization_id, created_at)</c>).
    /// </summary>
    public static IReadOnlyList<AuditLogEntryView> Project(IEnumerable<AuditLogEntry> entries, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return CanViewAuditLog(role)
            ? entries.Select(AuditLogEntryView.From).ToArray()
            : [];
    }
}
