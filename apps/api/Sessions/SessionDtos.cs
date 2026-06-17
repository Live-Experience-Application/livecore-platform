// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Sessions;

/// <summary>
/// Request body for creating a session (CORE-API-003,
/// <c>POST /api/v1/workspaces/{workspaceId}/sessions</c>, csv/api_routes.csv "Create
/// session", roles Owner,Admin,Host,CoHost).
///
/// The target organization is supplied as <see cref="OrganizationSlug"/> and the
/// target workspace is taken from the route path, mirroring how the scene create
/// (<c>POST /api/v1/workspaces/{workspaceId}/scenes</c>) resolves its tenant: the
/// slug is matched against the caller's token organization claim AND a persisted
/// organization membership by the tenant context resolver, and the create is then
/// authorized by the caller's role in the route's workspace (threat T5).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md): a session
/// carries only a human-readable <see cref="Title"/>. It deliberately carries NO
/// lifecycle status: a session is always created <see cref="SessionStatus.Prepared"/>
/// server-side (the state behind <c>SessionCreated</c>, docs/09_EVENT_CATALOG.md), so
/// a client can never create a session that is already live or ended — the only way
/// into the live timeline is the guarded start command. It also carries no id, tenant
/// id or timestamps: those are server-assigned.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the target workspace, used to resolve
/// the tenant context (the route carries no organization in its path).
/// </param>
/// <param name="Title">Human-readable display title of the new session.</param>
public sealed record CreateSessionRequest(string? OrganizationSlug, string? Title);

/// <summary>
/// Response projection of a session (CORE-SES-004,
/// <c>POST /api/v1/sessions/{sessionId}/start</c> and
/// <c>POST /api/v1/sessions/{sessionId}/end</c>; CORE-LIFE-010,
/// <c>POST /api/v1/sessions/{sessionId}/cancel</c>). It is the body returned by the
/// start/end/cancel lifecycle commands: the generic, product-neutral, server-side
/// view of the session AFTER the transition has been applied and persisted (a
/// cancelled session is returned with its new <see cref="SessionStatus.Cancelled"/>
/// status and its still-null live-timeline timestamps, since it never ran).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md,
/// docs/08_API_CONTRACTS.md DTO design rules): identifiers, the tenant and
/// workspace boundaries, the display title, the lifecycle status and the
/// server timestamps only. It carries:
/// <list type="bullet">
///   <item>NO host-only or hidden fields and NO participant-only fields — the
///   session is a single generic resource, not split into host/participant
///   projections here, and it has no hidden content to leak (docs/08 DTO rules;
///   threat T7 in docs/07_SECURITY_THREAT_MODEL.md).</item>
///   <item>NO internal authorization rationale — it never echoes why the caller
///   was allowed or how the tenant/workspace was resolved (docs/08; threat
///   T7).</item>
///   <item>The <see cref="Status"/> as the stable enum NAME
///   (Prepared/Live/Ended/Cancelled), never the in-memory numeric discriminator,
///   mirroring how the status is persisted (<see cref="SessionConfiguration"/>) and
///   how <c>WorkspaceInvitationResponse</c> projects its role/status.</item>
/// </list>
///
/// Server timestamps are included per docs/08 ("Include server timestamps"):
/// <see cref="CreatedAt"/>/<see cref="UpdatedAt"/> are the row's audit
/// timestamps, and the nullable <see cref="StartedAt"/>/<see cref="EndedAt"/> are
/// the live-timeline boundaries (null until the session starts/ends), so a client
/// can see exactly which transition has occurred.
///
/// There is deliberately NO resource version/ETag field: the <see cref="Session"/>
/// aggregate carries no concurrency token yet (CORE-SES-002 did not add one), and
/// inventing one here would be speculative. docs/08 asks for a version "where
/// concurrent updates matter"; the state machine's invalid-transition guard
/// (409 on a re-start/re-end) already prevents the duplicate side effects two
/// racing start/end commands could otherwise cause, so optimistic concurrency on
/// the session row is noted as a follow-up rather than fabricated here.
/// </summary>
/// <param name="Id">Surrogate id of the session (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the session belongs to.</param>
/// <param name="WorkspaceId">Workspace the session belongs to.</param>
/// <param name="Title">Human-readable display title of the session.</param>
/// <param name="Status">
/// Lifecycle status name (Prepared/Live/Ended/Cancelled) after the applied transition.
/// </param>
/// <param name="StartedAt">
/// When the live timeline started (UTC), or <see langword="null"/> while the
/// session is still Prepared.
/// </param>
/// <param name="EndedAt">
/// When the live timeline ended (UTC), or <see langword="null"/> until the
/// session is Ended.
/// </param>
/// <param name="CreatedAt">When the session was created (UTC).</param>
/// <param name="UpdatedAt">When the session was last updated (UTC).</param>
public sealed record SessionResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    string Title,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="Session"/> aggregate into its response DTO. Only the
    /// generic, non-sensitive fields are copied; the status is emitted as its
    /// stable name.
    /// </summary>
    public static SessionResponse From(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionResponse(
            session.Id,
            session.OrganizationId,
            session.WorkspaceId,
            session.Title,
            session.Status.ToString(),
            session.StartedAt,
            session.EndedAt,
            session.CreatedAt,
            session.UpdatedAt);
    }
}

/// <summary>
/// Response projection of a participant presence command (CORE-PRS-001,
/// <c>POST /api/v1/sessions/{sessionId}/participants/{participantId}/join</c> and
/// <c>.../leave</c>). It is the body returned when a host admits a participant to a session or removes one
/// from it, after the <see cref="SessionParticipantJoinService"/> / <see cref="SessionParticipantLeaveService"/>
/// has applied the transition, persisted the durable presence event and delivered it to the session audience.
///
/// The DTO is IDENTIFIER-ONLY and product-neutral (docs/04_PRODUCT_BOUNDARIES.md; docs/08_API_CONTRACTS.md DTO
/// design rules; threat T7 in docs/07_SECURITY_THREAT_MODEL.md): the session and participant surrogate ids and
/// the generic outcome NAME only. It deliberately carries NO participant display name or any other PII (the
/// whole point of the PII-free presence contract), NO internal authorization rationale, and NO hidden fields —
/// exactly the same identifier-only envelope the persisted <c>ParticipantJoined</c>/<c>ParticipantLeft</c> event
/// payload carries (<see cref="ParticipantPresenceEvent"/>).
/// </summary>
/// <param name="SessionId">Surrogate id of the session the presence change applied to.</param>
/// <param name="ParticipantId">Surrogate id of the participant that joined or left.</param>
/// <param name="Outcome">
/// Generic presence outcome name: <c>Joined</c> (admitted), <c>Left</c> (removed and the
/// <c>ParticipantLeft</c> event delivered) or <c>AlreadyLeft</c> (the idempotent no-op — the participant had
/// already left, so nothing changed and no event was emitted).
/// </param>
public sealed record ParticipantPresenceResponse(Guid SessionId, Guid ParticipantId, string Outcome);
