namespace LiveCore.Api.Audit;

/// <summary>
/// Safe, generic READ view of one <see cref="AuditLogEntry"/> returned to a caller authorized to view the
/// append-only audit log (CORE-AUD-005, the "audit query permissions" story). It is the audit analogue of
/// the export/recap views (<see cref="LiveCore.Api.Exports.ExportJobView"/>,
/// <see cref="LiveCore.Api.Recaps.RecapView"/>) — the product-neutral projection an authorized reader
/// receives once <see cref="AuditQueryPolicy"/> has granted access.
///
/// ONE SHAPE, NOT TWO. Unlike the export/recap projections — which return a FULL shape to host/metadata
/// roles and a STRIPPED shape to the audience — the audit log has NO audience shape at all: the "View audit
/// log" row of docs/06_AUTHORIZATION_MATRIX.md is a binary ACCESS grant (Owner/Admin/Auditor = yes; everyone
/// else = no), not a content-vs-metadata split. A caller either may read the audit log (and receives this
/// full view) or may not (and receives nothing — <see cref="AuditQueryPolicy.Project"/> yields an empty
/// list). There is therefore deliberately no <c>AuditLogEntrySummaryView</c>: an unauthorized role is denied
/// outright, never handed a reduced audit record.
///
/// NO SENSITIVE CONTENT (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). Every field is an identifier, an
/// enum or a generic state NAME (Hidden/Visible) — exactly the safe set the aggregate itself exposes through
/// its log-safe <see cref="AuditLogEntry.ToString"/>. The audit log records the FACT of a security-relevant
/// action (the tenant, the optional workspace, the action, the actor, the governed resource, the optional
/// selected-participant target and the optional before/after state), never the revealed scene/content body,
/// so this view never carries one. It is faithful to the recorded fact — a system action surfaces a
/// <see langword="null"/> actor, an organization-level action a <see langword="null"/> workspace, a
/// non-transition action a <see langword="null"/> state pair — and adds no internal authorization rationale
/// (docs/08_API_CONTRACTS.md).
/// </summary>
/// <param name="Id">Surrogate id of the audit entry (UUIDv7, time-ordered).</param>
/// <param name="OrganizationId">Tenant the audited action happened in, or <see langword="null"/> for a platform-level action.</param>
/// <param name="WorkspaceId">The workspace the action happened in, or <see langword="null"/> for an organization-level action.</param>
/// <param name="Action">The kind of security-relevant action recorded.</param>
/// <param name="ActorUserProfileId">The user that performed the action, or <see langword="null"/> for a system action.</param>
/// <param name="ResourceType">The governed resource kind name, or <see langword="null"/> when the action concerned no resource.</param>
/// <param name="ResourceId">The governed resource id, paired with <paramref name="ResourceType"/> or both <see langword="null"/>.</param>
/// <param name="TargetParticipantId">The selected participant of a private action, or <see langword="null"/> for an audience-wide action.</param>
/// <param name="PreviousState">The generic state name before the action, or <see langword="null"/>.</param>
/// <param name="NewState">The generic state name after the action, or <see langword="null"/> when the action is not a transition.</param>
/// <param name="CreatedAt">When the audited action happened (UTC).</param>
public sealed record AuditLogEntryView(
    Guid Id,
    Guid? OrganizationId,
    Guid? WorkspaceId,
    AuditAction Action,
    Guid? ActorUserProfileId,
    string? ResourceType,
    Guid? ResourceId,
    Guid? TargetParticipantId,
    string? PreviousState,
    string? NewState,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Projects an <see cref="AuditLogEntry"/> aggregate into its read view. Every recorded field is copied
    /// verbatim — the audit log stores only identifiers, enums and generic state names, so there is no
    /// content to leave behind (threat T7).
    /// </summary>
    public static AuditLogEntryView From(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AuditLogEntryView(
            entry.Id,
            entry.OrganizationId,
            entry.WorkspaceId,
            entry.Action,
            entry.ActorUserProfileId,
            entry.ResourceType,
            entry.ResourceId,
            entry.TargetParticipantId,
            entry.PreviousState,
            entry.NewState,
            entry.CreatedAt);
    }
}
