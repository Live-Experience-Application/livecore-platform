// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// The data-subject erasure command of the IdentityAccess module (CORE-PRIV-001, the "Privacy and Data
/// Lifecycle" epic, GDPR Art.17 "right to erasure"). An authorized Owner/Admin can erase a data subject's
/// personal data; this service removes the subject's user profile and ANONYMIZES the personal data the profile's
/// deletion does not itself reach, then appends an append-only, PII-free audit record — all inside ONE database
/// transaction so the erasure is applied whole or not at all.
///
/// WHAT IS ERASED. A data subject's personal data is spread across the schema, so the erasure is cross-cutting:
/// <list type="bullet">
///   <item>the <c>users</c> profile row — the subject's OIDC subject, email and display name — is HARD-deleted
///   (<see cref="IUserProfileRepository.EraseAsync"/>): the profile IS the PII, so it is removed outright;</item>
///   <item>the subject's <c>participants.display_name</c> values are scrubbed and their user links cleared
///   (<see cref="IParticipantRepository.AnonymizeBySubjectAsync"/>) — the display name is PII the user
///   foreign key cannot reach, so it must be anonymized explicitly;</item>
///   <item>the subject's <c>workspace_invitations.invited_email</c> rows are scrubbed
///   (<see cref="IWorkspaceInvitationRepository.AnonymizeByInvitedEmailAsync"/>) — the invited email is PII no
///   foreign key references, matched by the subject's own email value.</item>
/// </list>
///
/// WHAT THE SCHEMA'S FOREIGN KEYS DO (the cascade the erasure relies on). Deleting the user row lets the
/// database honor the foreign keys other modules already declared against <c>users(id)</c>:
/// <c>assets.created_by</c> and <c>export_jobs.requested_by</c> are <c>ON DELETE SET NULL</c>, so those records
/// SURVIVE with a null (anonymized) creator; <c>organization_members.user_id</c> and
/// <c>workspace_members.user_id</c> are <c>ON DELETE CASCADE</c>, so the subject's access grants are revoked
/// everywhere. The append-only <c>audit_logs</c> reference the actor/resource as RECORDED FACTS, not foreign
/// keys, so the PII-free audit trail and its per-tenant hash chain survive intact — which is precisely what
/// makes erasure reconcilable with the immutable audit log.
///
/// SCOPE / AUTHORIZATION. The command is AUTHORIZED within a tenant (the endpoint resolves an Owner/Admin and a
/// target member of that tenant before calling in, exactly like the member-removal command), but its EFFECT is
/// global, because a data subject's user profile is a single, deployment-wide identity (the <c>users</c> table
/// is global scope, no <c>organization_id</c>): erasing it removes the subject from the platform and scrubs
/// their per-tenant personal-data copies EVERYWHERE they were stored, as GDPR Art.17 requires. In a
/// multi-tenant deployment this is a data-controller-level operation; the controller/processor split and the
/// finer retention policy are documented as a privacy concern (docs/07_SECURITY_THREAT_MODEL.md; the privacy
/// doc, CORE-PRIV-005). The endpoint above performs the authentication, tenant resolution and Owner/Admin
/// authorization; this service is the authorized command's effect.
///
/// FAIL-CLOSED ORPHAN GUARD. Because erasing the user CASCADE-removes their memberships across every tenant,
/// erasing the sole Owner of an organization would leave that tenant permanently unreachable. The command
/// refuses (<see cref="DataSubjectErasureResult.SubjectIsSoleOrganizationOwner"/>) when the subject is the sole
/// Owner of any organization (the generalized last-Owner invariant,
/// <see cref="IOrganizationMemberRepository.IsSoleOwnerOfAnyOrganizationAsync"/>) and changes nothing.
///
/// ATOMICITY. The anonymizations, the profile delete and the audit append run inside one explicit transaction
/// over the shared <see cref="LiveCoreDbContext"/> (<see cref="TransactionalUnitOfWork"/>, CORE-CONC-002), so
/// either the whole erasure commits together or a failure rolls the lot back leaving every record intact and no
/// audit row written. Auditing is inside the transaction on purpose: an erasure is recorded as a fact or it does
/// not happen.
/// </summary>
internal sealed class DataSubjectErasureService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IUserProfileRepository _users;
    private readonly IParticipantRepository _participants;
    private readonly IWorkspaceInvitationRepository _invitations;
    private readonly IOrganizationMemberRepository _organizationMembers;
    private readonly IAuditLogRepository _audit;

    public DataSubjectErasureService(
        TransactionalUnitOfWork unitOfWork,
        IUserProfileRepository users,
        IParticipantRepository participants,
        IWorkspaceInvitationRepository invitations,
        IOrganizationMemberRepository organizationMembers,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(organizationMembers);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _users = users;
        _participants = participants;
        _invitations = invitations;
        _organizationMembers = organizationMembers;
        _audit = audit;
    }

    /// <summary>
    /// Erases the data subject identified by <paramref name="subjectUserProfileId"/>, anonymizing their
    /// participant display data and invited-email rows and deleting their user profile (whose foreign keys then
    /// anonymize their exports/assets and revoke their memberships), and appends an append-only
    /// <see cref="AuditAction.UserProfileErased"/> record in the requesting tenant — all atomically. Returns
    /// <see cref="DataSubjectErasureResult.Erased"/> on success,
    /// <see cref="DataSubjectErasureResult.SubjectNotFound"/> when no profile exists for the id, or
    /// <see cref="DataSubjectErasureResult.SubjectIsSoleOrganizationOwner"/> (changing nothing) when the subject
    /// is the sole Owner of an organization.
    /// </summary>
    /// <param name="organizationId">The tenant the erasure is authorized in (the audit scope; checked by the endpoint).</param>
    /// <param name="actorUserProfileId">The authenticated admin who executed the erasure — the audited actor.</param>
    /// <param name="subjectUserProfileId">The data subject's user-profile id (resolved from the target member).</param>
    /// <param name="now">The command timestamp (the anonymization and audit time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, actor id or subject id is empty.
    /// </exception>
    public async Task<DataSubjectErasureResult> EraseAsync(
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

        // Load the subject profile (read-only): it gives the email the invited-email scrub matches and proves
        // the subject exists. A missing profile reveals nothing and changes nothing — a safe NotFound, and no
        // transaction is opened for it.
        var subject = await _users.FindByIdAsync(subjectUserProfileId, cancellationToken).ConfigureAwait(false);
        if (subject is null)
        {
            return DataSubjectErasureResult.SubjectNotFound;
        }

        // Orphan guard (read-only): erasing the subject CASCADE-removes their memberships across every tenant,
        // so refuse before touching anything when the subject is the sole Owner of any organization (an
        // ownerless tenant is permanently unreachable). This is an invariant conflict, not an authorization
        // failure.
        if (await _organizationMembers
            .IsSoleOwnerOfAnyOrganizationAsync(subjectUserProfileId, cancellationToken)
            .ConfigureAwait(false))
        {
            return DataSubjectErasureResult.SubjectIsSoleOrganizationOwner;
        }

        // The subject's email value is captured before the transaction so a retry (which clears the change
        // tracker and detaches the loaded profile) still has it; it may be null when the provider never
        // asserted an email, in which case there are no invited-email rows to scrub.
        var subjectEmail = subject.Email;

        // ONE unit of work (CORE-CONC-002): the anonymizations, the profile delete and the audit append commit
        // together or roll back together, so an erasure is applied whole or not at all. Every injected
        // repository writes through the same scoped DbContext the unit of work begins the transaction on.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // Anonymize the PII the user foreign key cannot reach, cross-tenant by design (GDPR Art.17:
                // erase the subject's personal data EVERYWHERE it was stored).
                await _participants
                    .AnonymizeBySubjectAsync(subjectUserProfileId, now, transactionCancellationToken)
                    .ConfigureAwait(false);

                if (subjectEmail is not null)
                {
                    await _invitations
                        .AnonymizeByInvitedEmailAsync(subjectEmail, now, transactionCancellationToken)
                        .ConfigureAwait(false);
                }

                // Delete the profile row; the schema's ON DELETE SET NULL foreign keys then anonymize the
                // subject's exports/assets (the records survive with a null creator) and the ON DELETE CASCADE
                // foreign keys revoke their org/workspace memberships.
                await _users.EraseAsync(subject, transactionCancellationToken).ConfigureAwait(false);

                // AUDIT (the story's "audited by id only" criterion): an erasure is security-relevant, so append
                // an append-only record capturing the actor (the admin who erased), the erased subject (by id)
                // and the requesting tenant. The audit references are recorded facts, not foreign keys, so the
                // row outlives the now-deleted profile and never carries the erased PII (threats T1/T5/T7).
                var entry = AuditLogEntry.ForUserProfileErasure(
                    organizationId,
                    actorUserProfileId,
                    nameof(UserProfile),
                    subjectUserProfileId,
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                return DataSubjectErasureResult.Erased;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
