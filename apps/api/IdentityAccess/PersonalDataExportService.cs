using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// The data-subject access and portability export command of the IdentityAccess module (CORE-PRIV-004, the
/// "Privacy and Data Lifecycle" epic, GDPR Art.15 "right of access" / Art.20 "right to data portability"). It is
/// the ACCESS counterpart of <see cref="DataSubjectErasureService"/> (the erasure half, CORE-PRIV-001): where an
/// erasure REMOVES a data subject's personal data, this command READS it back into a machine-readable export for
/// the entitled recipient — the data subject themselves, or an Owner/Admin acting on their behalf (the endpoint above
/// performs that authorization). It is DISTINCT from the session/workspace Exports feature, which exports content
/// artifacts, not a subject's personal data.
///
/// WHAT IS ASSEMBLED. A data subject's personal data is spread across the schema, so the export is cross-cutting —
/// it gathers exactly the documented set (the story's acceptance criteria): the subject's user-profile identity,
/// their organization membership in the tenant, their workspace memberships, their participant records and the
/// invitations addressed to their email. The same repositories the erasure command reuses are reused here for the
/// reads (the user-profile, organization-membership, workspace-membership, participant and invitation
/// repositories), so there is no parallel persistence path.
///
/// TENANT SCOPE (threat T5). Unlike the erasure — whose EFFECT is global because a subject's personal data must be
/// removed everywhere (GDPR Art.17) — this export is TENANT-SCOPED in both authorization AND data: the export
/// route resolves a single tenant, and every collection is read scoped to that resolved organization (the
/// repositories lead their predicates with <c>organization_id</c>). So an Owner/Admin exporting on the subject's
/// behalf never learns of the subject's activity in a tenant they do not control, and the subject reaches their
/// data in other tenants only through those tenants' own export routes. The single GLOBAL datum is the user
/// profile itself (one deployment-wide identity, no tenant) — but it is the subject's OWN identity, disclosed to
/// the subject themselves or the tenant's data controller acting for them, exactly what GDPR access requires.
///
/// AUDIT (the story's "audited" criterion; threats T1/T5/T7). Disclosing personal data is security-relevant, so a
/// successful export appends an append-only <see cref="AuditAction.PersonalDataExported"/> record capturing WHO
/// obtained the export (the actor — the subject or the admin) and WHOSE personal data was disclosed (the subject,
/// BY ID), in the requesting tenant. The audit row carries ONLY identifiers — never the disclosed email, display
/// names, invited emails or any of the exported personal data: the PII lives only in the export RESPONSE delivered
/// to the entitled subject, never in the audit log, so the PII-free per-tenant hash chain stays intact and the
/// audit row outlives a later erasure of the subject (the reference is a recorded fact, not a foreign key).
///
/// NO TRANSACTION. The export is a read of the subject's data plus one append-only audit insert; it mutates no
/// business data, so unlike the erasure it opens no explicit unit of work. The audit append is its own save.
/// </summary>
internal sealed class PersonalDataExportService
{
    private readonly IUserProfileRepository _users;
    private readonly IOrganizationMemberRepository _organizationMembers;
    private readonly IWorkspaceMemberRepository _workspaceMembers;
    private readonly IParticipantRepository _participants;
    private readonly IWorkspaceInvitationRepository _invitations;
    private readonly IAuditLogRepository _audit;

    public PersonalDataExportService(
        IUserProfileRepository users,
        IOrganizationMemberRepository organizationMembers,
        IWorkspaceMemberRepository workspaceMembers,
        IParticipantRepository participants,
        IWorkspaceInvitationRepository invitations,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(organizationMembers);
        ArgumentNullException.ThrowIfNull(workspaceMembers);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(audit);
        _users = users;
        _organizationMembers = organizationMembers;
        _workspaceMembers = workspaceMembers;
        _participants = participants;
        _invitations = invitations;
        _audit = audit;
    }

    /// <summary>
    /// Assembles the data subject's tenant-scoped personal-data export and audits the access. Returns the
    /// assembled <see cref="PersonalDataExport"/> on success, or <see langword="null"/> when no user profile
    /// exists for <paramref name="subjectUserProfileId"/> (the endpoint hides that as a fail-closed 404; it should
    /// not occur once a tenant-scoped membership has been resolved, since the membership references a real user
    /// profile by foreign key, but the command fails closed rather than assuming).
    /// </summary>
    /// <param name="organizationId">The tenant the export is authorized in (the scope of every tenant-scoped read; the audit tenant).</param>
    /// <param name="actorUserProfileId">The authenticated caller who obtained the export — the subject or an admin acting for them (the audited actor).</param>
    /// <param name="subjectUserProfileId">The data subject's user-profile id (resolved from the target member).</param>
    /// <param name="now">The command timestamp (the audit time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The organization id, actor id or subject id is empty.</exception>
    public async Task<PersonalDataExport?> ExportAsync(
        Guid organizationId,
        Guid actorUserProfileId,
        Guid subjectUserProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (subjectUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject user profile id must not be empty.", nameof(subjectUserProfileId));
        }

        // Load the subject's global profile (the subject's own identity, and the email the invitation read
        // matches). A missing profile discloses nothing and is a safe NotFound (null).
        var subject = await _users.FindByIdAsync(subjectUserProfileId, cancellationToken).ConfigureAwait(false);
        if (subject is null)
        {
            return null;
        }

        // The subject's personal data within THIS tenant (every read tenant-scoped, leading with organization_id —
        // threat T5). The organization membership is the single (organization, subject) row; the rest are the
        // subject's records across the tenant's workspaces.
        var organizationMembership = await _organizationMembers
            .FindAsync(organizationId, subjectUserProfileId, cancellationToken)
            .ConfigureAwait(false);

        var workspaceMemberships = await _workspaceMembers
            .ListBySubjectInOrganizationAsync(organizationId, subjectUserProfileId, cancellationToken)
            .ConfigureAwait(false);

        var participants = await _participants
            .ListBySubjectInOrganizationAsync(organizationId, subjectUserProfileId, cancellationToken)
            .ConfigureAwait(false);

        // Invitations are addressed by EMAIL (an invitation can precede the subject ever holding a
        // profile/membership), so they are matched by the subject's own email value. A profile with no email has
        // no invited-email rows to disclose.
        var invitations = subject.Email is { } subjectEmail
            ? await _invitations
                .ListByInvitedEmailInOrganizationAsync(organizationId, subjectEmail, cancellationToken)
                .ConfigureAwait(false)
            : Array.Empty<WorkspaceInvitation>();

        // AUDIT (by id only): the access is recorded as an append-only fact capturing the actor (who obtained the
        // export), the exported subject (by id) and the requesting tenant — never the disclosed PII (threats
        // T1/T5/T7). It is appended only after a successful assembly, so a not-found subject records nothing.
        var entry = AuditLogEntry.ForPersonalDataExport(
            organizationId,
            actorUserProfileId,
            nameof(UserProfile),
            subjectUserProfileId,
            now);
        await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        return new PersonalDataExport(
            organizationId,
            subject,
            organizationMembership,
            workspaceMemberships,
            participants,
            invitations);
    }
}
