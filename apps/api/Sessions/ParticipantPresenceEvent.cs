using System.Text.Json;
using LiveCore.Api.Realtime;

namespace LiveCore.Api.Sessions;

/// <summary>
/// Builds the durable <c>ParticipantJoined</c> / <c>ParticipantLeft</c> session event of the participant
/// presence flows (CORE-EVT-002) so the join flow
/// (<see cref="SessionParticipantJoinService"/>) and the leave flow
/// (<see cref="SessionParticipantLeaveService"/>) compose the SAME identifier-only event the SAME way,
/// rather than duplicating the payload shape and routing decision.
///
/// The presence events are SUBJECTLESS AUDIENCE events — exactly like the CORE-EVT-001
/// <c>SessionStarted</c>/<c>SessionEnded</c> events: no visibility subject and no selected participant, so
/// the reused recipient resolver (<see cref="ISessionEventRecipientResolver"/>) delivers each unconditionally
/// to the whole session audience — the hosts (always, so the event is host-visible,
/// docs/06_AUTHORIZATION_MATRIX.md), the observers and every active participant (the configurable audience of
/// docs/09_EVENT_CATALOG.md). Routing the presence by its participant SUBJECT (the
/// <see cref="SessionEventPayloads.ParticipantPresenceEventPayload.ParticipantId"/> in the payload) rather
/// than by a selected-participant target keeps the announcement an audience event while the recipient-safe
/// envelope still hides the internal addressing fields.
///
/// PAYLOAD IS IDENTIFIER-ONLY (threat T7 in docs/07_SECURITY_THREAT_MODEL.md): it carries the participant's
/// surrogate id and nothing else — never the participant's display name or any other PII. The participant id
/// is an opaque surrogate (not a name, not a user identity), so the audience learns only WHICH participant
/// (by id) joined or left, which is the whole point of a presence event, without leaking who they are.
/// </summary>
internal static class ParticipantPresenceEvent
{
    /// <summary>The presence-event payload schema version (docs/09_EVENT_CATALOG.md).</summary>
    private const int _schemaVersion = 1;

    /// <summary>
    /// Composes a presence <see cref="SessionEvent"/> of the given <paramref name="eventType"/>
    /// (<see cref="SessionEventTypes.ParticipantJoined"/> or <see cref="SessionEventTypes.ParticipantLeft"/>)
    /// for the participant identified by <paramref name="participantId"/>, in the given session (owned by the
    /// given workspace and tenant). The event is audience-wide (no selected-participant target) and carries no
    /// visibility subject, so it is delivered unconditionally to the whole session audience. The actor
    /// <paramref name="createdBy"/> is the user who caused the event (the joining participant's linked user) or
    /// <see langword="null"/> for a system-emitted event (an anonymous participant's join, or any leave). The
    /// payload is the participant id only — never any display name or PII (threat T7).
    /// </summary>
    public static SessionEvent Create(
        string eventType,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        Guid? createdBy,
        DateTimeOffset createdAt)
    {
        var payload = JsonSerializer.Serialize(new SessionEventPayloads.ParticipantPresenceEventPayload(participantId));

        // The event is audience-wide (targetParticipantId null, not a single selected participant) and carries
        // no visibility subject (both subject parameters default to null), so it is an unconditional audience
        // event — the recipient resolver delivers it to the whole session audience without gating it through
        // the Visibility engine, exactly like SessionStarted/SessionEnded.
        return SessionEvent.Create(
            organizationId,
            workspaceId,
            sessionId,
            eventType,
            createdBy,
            targetParticipantId: null,
            payload,
            _schemaVersion,
            createdAt);
    }
}
