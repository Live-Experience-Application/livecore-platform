// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Realtime;

/// <summary>
/// FULL, host/metadata-facing projection of a session's participant roster + presence (CORE-PRS-002, the
/// "Vertical Adopter Consumability Completeness" epic). It is the complete, product-neutral view of who is in
/// the session and who is currently connected, returned to the roles that may view host-only content
/// (Owner/Admin/Host/CoHost — the "View host-only content" = <c>yes</c> rows of
/// docs/06_AUTHORIZATION_MATRIX.md, classified by the central <see cref="VisibilityRoles"/>).
///
/// This is one of the TWO distinct roster view types (docs/08_API_CONTRACTS.md DTO design rule "Host DTOs and
/// Participant DTOs are different"): the full view — the ONLY view that carries the host-only
/// <see cref="SessionRosterParticipant.UserProfileId"/> user link — while <see cref="ParticipantRosterView"/>
/// is the host-only-field-stripped, audience-safe view. <see cref="SessionRosterProjection"/> selects between
/// them from the caller's workspace role; this type is NEVER produced for an audience (Participant/Observer/
/// Auditor) role, because the user-account link behind a participant is host-only metadata an audience member
/// may not see (threat T2 visibility leak in docs/07_SECURITY_THREAT_MODEL.md).
///
/// The roster is the session AUDIENCE: a participant is workspace-scoped and there is no persisted
/// session-participant roster, so a session's roster is its workspace's ACTIVE participants — the same
/// population a participant realtime connection is admitted from (docs/11_REALTIME_SYNC.md). The view carries
/// no internal authorization rationale (docs/08).
/// </summary>
/// <param name="SessionId">The session whose roster this is.</param>
/// <param name="Participants">The session's active participants, each with its presence flag.</param>
public sealed record SessionRosterView(
    Guid SessionId,
    IReadOnlyList<SessionRosterParticipant> Participants);

/// <summary>
/// One participant of the FULL host roster (<see cref="SessionRosterView"/>). It carries the participant's
/// surrogate id, its session-facing display identity, the HOST-ONLY link to the authenticated user behind it
/// (or <see langword="null"/> for an anonymous/guest participant) and whether the participant currently holds
/// a live realtime connection to the session.
/// </summary>
/// <param name="ParticipantId">Surrogate id of the participant (UUIDv7).</param>
/// <param name="DisplayName">The participant's session-facing display identity (audience-facing metadata).</param>
/// <param name="UserProfileId">
/// HOST-ONLY: the authenticated user this participant represents, or <see langword="null"/> for an
/// anonymous/guest participant. Stripped from the audience projection (<see cref="ParticipantRosterParticipant"/>)
/// so an audience member never learns which user account backs another participant (threat T2/T7).
/// </param>
/// <param name="Present">
/// Whether the participant currently holds at least one live realtime connection to the session on this API
/// instance (the presence signal; docs/11_REALTIME_SYNC.md).
/// </param>
public sealed record SessionRosterParticipant(
    Guid ParticipantId,
    string DisplayName,
    Guid? UserProfileId,
    bool Present);

/// <summary>
/// AUDIENCE-safe, host-only-field-STRIPPED projection of a session's participant roster + presence
/// (CORE-PRS-002). It is the second of the TWO roster view types: the shape produced for the audience
/// workspace roles (Participant/Observer — and, fail-closed, any non-host role) instead of the full
/// <see cref="SessionRosterView"/>. docs/08_API_CONTRACTS.md states "Host DTOs and Participant DTOs are
/// different" and "Participant DTOs must not contain hidden content fields", so it is a SEPARATE record type,
/// not a reused/optional-field variant of the full view.
///
/// It surfaces exactly what a "who is present" audience UI needs — each participant's id, session-facing
/// display name and presence flag — and OMITS the host-only user-account link
/// (<see cref="SessionRosterParticipant.UserProfileId"/>): an audience member sees who is present by their
/// display identity but never which authenticated user backs them (threat T2 visibility leak). Adding any
/// host-only field here would be caught by the projection tests that assert its EXACT property set.
/// </summary>
/// <param name="SessionId">The session whose roster this is.</param>
/// <param name="Participants">The session's active participants, each with its presence flag (no host-only fields).</param>
public sealed record ParticipantRosterView(
    Guid SessionId,
    IReadOnlyList<ParticipantRosterParticipant> Participants);

/// <summary>
/// One participant of the AUDIENCE-safe roster (<see cref="ParticipantRosterView"/>). It carries only the
/// non-sensitive id, the session-facing display identity and the presence flag; the host-only user-account
/// link is deliberately absent (see <see cref="ParticipantRosterView"/>).
/// </summary>
/// <param name="ParticipantId">Surrogate id of the participant (UUIDv7); a non-sensitive handle.</param>
/// <param name="DisplayName">The participant's session-facing display identity (audience-facing metadata).</param>
/// <param name="Present">
/// Whether the participant currently holds at least one live realtime connection to the session on this API
/// instance (the presence signal; docs/11_REALTIME_SYNC.md).
/// </param>
public sealed record ParticipantRosterParticipant(
    Guid ParticipantId,
    string DisplayName,
    bool Present);

/// <summary>
/// Role-based session-roster PROJECTOR (CORE-PRS-002) — the pure mapping that honors the host-vs-audience
/// separation for the host-only participant fields (threat T2 visibility leak in
/// docs/07_SECURITY_THREAT_MODEL.md; the "View host-only content" rows of docs/06_AUTHORIZATION_MATRIX.md). It
/// selects WHICH of the two distinct roster view shapes (<see cref="SessionRosterView"/> full vs
/// <see cref="ParticipantRosterView"/> audience-safe) a caller receives, from the caller's WORKSPACE
/// <see cref="MembershipRole"/>, and stamps each participant's presence from the realtime-connection set.
///
/// It REUSES the central <see cref="VisibilityRoles"/> classification (docs/05_MODULE_CONTRACTS.md: the
/// Visibility module is the single source of truth for the host/audience partition, "do not duplicate
/// visibility logic elsewhere") rather than reinventing the role set: a role that
/// <see cref="VisibilityRoles.ViewsHostOnlyContent"/> (Owner/Admin/Host/CoHost) gets the FULL view WITH the
/// host-only user link; every other role — the audience roles Participant/Observer, the Auditor (audit-only on
/// content, never an automatic live-content grant) and any undefined value — falls closed to the stripped
/// audience view WITHOUT it. <see cref="MembershipRole"/> is NON-LINEAR (its integer values are storage
/// discriminators only and must never be compared with &gt;/&lt;), so the classification is an EXACT
/// set-membership check (inside <see cref="VisibilityRoles"/>), never an ordering comparison, and an unknown
/// role is denied the broader host view by default (fail-closed).
/// </summary>
internal static class SessionRosterProjection
{
    /// <summary>
    /// Projects the session's active <paramref name="participants"/> into the role-appropriate roster view,
    /// stamping each participant present iff its id is in <paramref name="presentParticipantIds"/>. Boxed as
    /// <see cref="object"/> so the endpoint can return either the full (<see cref="SessionRosterView"/>) or the
    /// audience (<see cref="ParticipantRosterView"/>) shape as the response body. A role that
    /// <see cref="VisibilityRoles.ViewsHostOnlyContent"/> receives the full view; every other role falls closed
    /// to the stripped audience view.
    /// </summary>
    public static object Project(
        Guid sessionId,
        IReadOnlyList<Participant> participants,
        IReadOnlySet<Guid> presentParticipantIds,
        MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(presentParticipantIds);

        if (VisibilityRoles.ViewsHostOnlyContent(role))
        {
            var hostEntries = participants
                .Select(participant => new SessionRosterParticipant(
                    participant.Id,
                    participant.DisplayName,
                    participant.UserProfileId,
                    presentParticipantIds.Contains(participant.Id)))
                .ToArray();
            return new SessionRosterView(sessionId, hostEntries);
        }

        var audienceEntries = participants
            .Select(participant => new ParticipantRosterParticipant(
                participant.Id,
                participant.DisplayName,
                presentParticipantIds.Contains(participant.Id)))
            .ToArray();
        return new ParticipantRosterView(sessionId, audienceEntries);
    }
}
