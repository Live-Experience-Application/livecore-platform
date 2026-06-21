// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Machine-readable response of a data-subject access/portability export
/// (CORE-PRIV-004, <c>GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export</c>,
/// GDPR Art.15 access / Art.20 portability). It is the projection of a tenant-scoped
/// <see cref="PersonalDataExport"/>: the personal data Core holds about ONE data subject within ONE tenant —
/// their identity profile, their organization membership, their workspace memberships, their participant records
/// and the invitations addressed to their email (the documented set from the story's acceptance criteria).
///
/// PII BY DESIGN, BUT NO SECRETS (threats T6/T7). Unlike the principal-context <see cref="CurrentPrincipalResponse"/>
/// or the audit views, this projection DELIBERATELY carries the subject's personal data — that is the entire point
/// of an access/portability export — and it is delivered ONLY to the entitled subject or an Owner/Admin acting on
/// their behalf (the endpoint authorizes that; an unentitled tenant member is denied 403). What it never carries
/// is a SECRET: no invitation token or token hash, no access token, no OIDC credential and no internal
/// authorization rationale. Every record is the subject's OWN data, scoped to the resolved tenant, so the export
/// never exposes a record belonging to another subject or another tenant (threat T5).
/// </summary>
/// <param name="OrganizationId">The tenant the export was produced within (the scope of every collection below).</param>
/// <param name="Subject">The subject's identity profile (the disclosed identity datum).</param>
/// <param name="OrganizationMembership">The subject's membership in this tenant, or <see langword="null"/> if none.</param>
/// <param name="WorkspaceMemberships">The subject's workspace memberships within this tenant.</param>
/// <param name="Participants">The subject's participant records within this tenant.</param>
/// <param name="Invitations">The invitations addressed to the subject's email within this tenant.</param>
/// <param name="PushSubscriptions">The subject's global Web Push subscriptions (per-principal personal data).</param>
public sealed record PersonalDataExportResponse(
    Guid OrganizationId,
    PersonalDataExportSubjectResponse Subject,
    PersonalDataExportOrganizationMembershipResponse? OrganizationMembership,
    IReadOnlyList<PersonalDataExportWorkspaceMembershipResponse> WorkspaceMemberships,
    IReadOnlyList<PersonalDataExportParticipantResponse> Participants,
    IReadOnlyList<PersonalDataExportInvitationResponse> Invitations,
    IReadOnlyList<PersonalDataExportPushSubscriptionResponse> PushSubscriptions)
{
    /// <summary>
    /// Projects the assembled <paramref name="export"/> into its machine-readable response. Internal because the
    /// assembled <see cref="PersonalDataExport"/> is an internal application result; the response record itself is
    /// public so its serialized shape is the documented contract.
    /// </summary>
    internal static PersonalDataExportResponse From(PersonalDataExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        return new PersonalDataExportResponse(
            export.OrganizationId,
            PersonalDataExportSubjectResponse.From(export.Subject),
            export.OrganizationMembership is { } organizationMembership
                ? PersonalDataExportOrganizationMembershipResponse.From(organizationMembership)
                : null,
            export.WorkspaceMemberships
                .Select(PersonalDataExportWorkspaceMembershipResponse.From)
                .ToArray(),
            export.Participants
                .Select(PersonalDataExportParticipantResponse.From)
                .ToArray(),
            export.Invitations
                .Select(PersonalDataExportInvitationResponse.From)
                .ToArray(),
            export.PushSubscriptions
                .Select(PersonalDataExportPushSubscriptionResponse.From)
                .ToArray());
    }
}

/// <summary>
/// The subject's identity profile within a personal-data export (CORE-PRIV-004): the surrogate profile id, the
/// OIDC identity pair (issuer + subject) that keys the profile, and the optional display metadata mirrored from
/// the provider. These ARE the subject's personal data (their account identity), disclosed to the entitled
/// subject under GDPR Art.15/20 — but never a credential or token (threat T7).
/// </summary>
/// <param name="Id">Surrogate id of the subject's user profile (UUIDv7).</param>
/// <param name="Issuer">Identity of the OIDC provider that asserted the subject.</param>
/// <param name="Subject">Issuer-local stable identifier of the subject.</param>
/// <param name="DisplayName">Optional human-readable name mirrored from the provider.</param>
/// <param name="Email">Optional email address mirrored from the provider.</param>
public sealed record PersonalDataExportSubjectResponse(
    Guid Id,
    string Issuer,
    string Subject,
    string? DisplayName,
    string? Email)
{
    /// <summary>Projects a <see cref="UserProfile"/> into its export DTO.</summary>
    public static PersonalDataExportSubjectResponse From(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new PersonalDataExportSubjectResponse(
            profile.Id,
            profile.Issuer,
            profile.SubjectId,
            profile.DisplayName,
            profile.Email);
    }
}

/// <summary>
/// The subject's organization membership within a personal-data export (CORE-PRIV-004): the generic role they
/// hold in the tenant and when it was granted. The role is the stable generic <see cref="MembershipRole"/> name,
/// product-neutral per docs/03_DOMAIN_LANGUAGE.md.
/// </summary>
/// <param name="Id">Surrogate id of the membership row.</param>
/// <param name="OrganizationId">The tenant the membership grants standing in.</param>
/// <param name="Role">The generic role the subject holds in the organization.</param>
/// <param name="CreatedAt">When the membership was created (UTC).</param>
public sealed record PersonalDataExportOrganizationMembershipResponse(
    Guid Id,
    Guid OrganizationId,
    string Role,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects an <see cref="OrganizationMember"/> into its export DTO.</summary>
    public static PersonalDataExportOrganizationMembershipResponse From(OrganizationMember member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new PersonalDataExportOrganizationMembershipResponse(
            member.Id,
            member.OrganizationId,
            member.Role.ToString(),
            member.CreatedAt);
    }
}

/// <summary>
/// The subject's workspace membership within a personal-data export (CORE-PRIV-004): the workspace they belong to,
/// the generic role they hold and when it was granted. The role is the stable generic
/// <see cref="MembershipRole"/> name.
/// </summary>
/// <param name="Id">Surrogate id of the membership row.</param>
/// <param name="WorkspaceId">The workspace the membership grants standing in.</param>
/// <param name="Role">The generic role the subject holds in the workspace.</param>
/// <param name="CreatedAt">When the membership was created (UTC).</param>
public sealed record PersonalDataExportWorkspaceMembershipResponse(
    Guid Id,
    Guid WorkspaceId,
    string Role,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="WorkspaceMember"/> into its export DTO.</summary>
    public static PersonalDataExportWorkspaceMembershipResponse From(WorkspaceMember member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new PersonalDataExportWorkspaceMembershipResponse(
            member.Id,
            member.WorkspaceId,
            member.Role.ToString(),
            member.CreatedAt);
    }
}

/// <summary>
/// The subject's participant record within a personal-data export (CORE-PRIV-004): the workspace they took part
/// in, their display identity (their personal datum on this record) and the participant's lifecycle status. The
/// status is the stable generic <see cref="ParticipantStatus"/> name.
/// </summary>
/// <param name="Id">Surrogate id of the participant row.</param>
/// <param name="WorkspaceId">The workspace the participant belongs to.</param>
/// <param name="DisplayName">The participant's display identity (the subject's personal datum on this record).</param>
/// <param name="Status">The participant's lifecycle status (e.g. Active / Removed).</param>
/// <param name="CreatedAt">When the participant was created (UTC).</param>
public sealed record PersonalDataExportParticipantResponse(
    Guid Id,
    Guid WorkspaceId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="Participant"/> into its export DTO.</summary>
    public static PersonalDataExportParticipantResponse From(Participant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        return new PersonalDataExportParticipantResponse(
            participant.Id,
            participant.WorkspaceId,
            participant.DisplayName,
            participant.Status.ToString(),
            participant.CreatedAt);
    }
}

/// <summary>
/// An invitation addressed to the subject within a personal-data export (CORE-PRIV-004): the workspace it scoped,
/// the invited email (the subject's personal datum), the generic role it offered, its lifecycle status and its
/// timestamps. The invite TOKEN and its hash are NEVER projected — the only secret on the aggregate stays out of
/// the export (threats T6/T7). The role and status are stable generic <see cref="MembershipRole"/> /
/// <see cref="WorkspaceInvitationStatus"/> names.
/// </summary>
/// <param name="Id">Surrogate id of the invitation row.</param>
/// <param name="WorkspaceId">The workspace the invitation scoped.</param>
/// <param name="InvitedEmail">The invited email address (the subject's personal datum).</param>
/// <param name="Role">The generic role the invitation offered.</param>
/// <param name="Status">The invitation's lifecycle status (e.g. Pending / Accepted / Revoked).</param>
/// <param name="ExpiresAt">When the invitation expires (UTC).</param>
/// <param name="CreatedAt">When the invitation was created (UTC).</param>
public sealed record PersonalDataExportInvitationResponse(
    Guid Id,
    Guid WorkspaceId,
    string InvitedEmail,
    string Role,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="WorkspaceInvitation"/> into its export DTO, never emitting the token hash.</summary>
    public static PersonalDataExportInvitationResponse From(WorkspaceInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        return new PersonalDataExportInvitationResponse(
            invitation.Id,
            invitation.WorkspaceId,
            invitation.InvitedEmail,
            invitation.Role.ToString(),
            invitation.Status.ToString(),
            invitation.ExpiresAt,
            invitation.CreatedAt);
    }
}

/// <summary>
/// A Web Push subscription registered by the subject within a personal-data export (CORE-PUSH-001): the
/// subscription id, the push service endpoint (the subject's per-device personal datum) and when it was
/// registered. The encryption keys (<c>p256dh</c> and the <c>auth</c> secret) are NEVER projected — the
/// subscription's secret stays out of the export, exactly as the invitation token hash does (threats T6/T7).
/// </summary>
/// <param name="Id">Surrogate id of the subscription row.</param>
/// <param name="Endpoint">The push service endpoint URL the subscription is registered against.</param>
/// <param name="CreatedAt">When the subscription was registered (UTC).</param>
public sealed record PersonalDataExportPushSubscriptionResponse(
    Guid Id,
    string Endpoint,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="PushSubscription"/> into its export DTO, never emitting the keys.</summary>
    public static PersonalDataExportPushSubscriptionResponse From(PushSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return new PersonalDataExportPushSubscriptionResponse(
            subscription.Id,
            subscription.Endpoint,
            subscription.CreatedAt);
    }
}
