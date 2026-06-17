// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// The recipient-safe envelope delivered to a connected client when a session event is published — the
/// "project payload" step of the documented delivery flow ("...-> project payload -> send to recipient
/// groups", docs/11_REALTIME_SYNC.md). It carries only what a recipient may see: the event id, the
/// per-session monotonic <see cref="Sequence"/> (CORE-RTC-001) by which the recipient orders the stream
/// and detects a missed event as a gap, the generic event type, the session it belongs to, the
/// server-composed payload (resource IDENTIFIERS only) and the schema version + timestamp. It deliberately
/// EXCLUDES the internal addressing fields of
/// the stored <see cref="SessionEvent"/> — the organization and workspace ids, the <c>createdBy</c>
/// actor and the <c>visibilitySubject</c> gating fields — so a recipient response never carries internal
/// authorization rationale (docs/08_API_CONTRACTS.md; threats T2/T7).
///
/// PER-RECIPIENT PROJECTION (CORE-RT-004, the docs/09_EVENT_CATALOG.md <c>visibilityProjection</c>). The
/// envelope is projected DIFFERENTLY for the two recipient classes so that "Participant events must
/// contain only data visible to that participant" (docs/11_REALTIME_SYNC.md):
/// <list type="bullet">
///   <item><see cref="ForAudience"/> — the AUDIENCE projection (participants and observers). Its
///   <see cref="TargetParticipantId"/> is <see langword="null"/>: an audience recipient must never learn
///   WHO ELSE a private reveal was targeted at (threats T2/T7), only the resource it may itself see.</item>
///   <item><see cref="ForHost"/> — the HOST projection (the session hosts group). It additionally
///   carries the routing <see cref="TargetParticipantId"/>, the "to whom" confirmation a host is entitled
///   to (docs/06_AUTHORIZATION_MATRIX.md grants hosts "Send private content"; docs/09_EVENT_CATALOG.md:
///   "host receives audit/confirmation event"). Host-content roles may see everything.</item>
/// </list>
/// Both projections share the same client method and the same recipient-safe payload; they differ only in
/// whether the host-only routing target is populated. Computing which recipients receive WHICH projection
/// (the fan-out and the per-recipient visibility gate) is <see cref="SessionEventRecipientResolver"/>.
/// </summary>
internal sealed record SessionEventEnvelope(
    Guid EventId,
    long Sequence,
    string EventType,
    Guid SessionId,
    string Payload,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    Guid? TargetParticipantId = null)
{
    /// <summary>
    /// The SignalR client method invoked on the recipient connections to deliver an event. A single,
    /// server-fixed method name; clients subscribe to it to receive session events.
    /// </summary>
    public const string ClientMethod = "SessionEvent";

    /// <summary>
    /// Projects a stored <see cref="SessionEvent"/> to its AUDIENCE-safe envelope (participants and
    /// observers): the routing target is omitted, so a recipient never learns who else was targeted.
    /// </summary>
    public static SessionEventEnvelope ForAudience(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        return new SessionEventEnvelope(
            sessionEvent.Id,
            sessionEvent.Sequence,
            sessionEvent.EventType,
            sessionEvent.SessionId,
            sessionEvent.Payload,
            sessionEvent.SchemaVersion,
            sessionEvent.CreatedAt,
            TargetParticipantId: null);
    }

    /// <summary>
    /// Projects a stored <see cref="SessionEvent"/> to its HOST envelope (the session hosts group):
    /// identical to the audience projection but additionally carrying the routing
    /// <see cref="TargetParticipantId"/> — the "to whom" confirmation hosts are entitled to.
    /// </summary>
    public static SessionEventEnvelope ForHost(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        return new SessionEventEnvelope(
            sessionEvent.Id,
            sessionEvent.Sequence,
            sessionEvent.EventType,
            sessionEvent.SessionId,
            sessionEvent.Payload,
            sessionEvent.SchemaVersion,
            sessionEvent.CreatedAt,
            TargetParticipantId: sessionEvent.TargetParticipantId);
    }
}
