// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// The identifier-only PAYLOAD contracts for the Core session events (CORE-RT-008): the typed shapes the
/// command sites serialize into a <see cref="SessionEvent.Payload"/> string and a consumer parses back out of
/// the recipient-safe envelope / reconnect-replay item. Each record is the SINGLE definition of its event's
/// payload — the reveal/hide endpoints, the session lifecycle endpoint, the participant presence flow and the
/// recap worker all compose their events from these records rather than from per-site duplicates — so a
/// payload shape lives in exactly one place.
///
/// <para>
/// CATALOG-AS-CONTRACT, EXTENDED TO PAYLOADS (CORE-RT-008, the payload analogue of CORE-EVT-004's
/// vocabulary binding). <see cref="ByEventType"/> maps EVERY <see cref="SessionEventTypes"/> constant to its
/// payload record, so the emitted vocabulary and its payload shapes are both machine-readable from one place.
/// The published TypeScript contract (<c>@livecore/contracts</c> <c>KnownSessionEventPayloadFields</c>) is
/// drift-gated against this file and <see cref="SessionEventTypes"/>: a CI gate fails if the TypeScript event
/// vocabulary or payload field set diverges from the non-deferred <c>csv/event_catalog.csv</c> events and
/// these records — the TypeScript-side mirror of spec-consistency check 11
/// (docs/23_PACKAGE_VERSIONING.md; docs/09_EVENT_CATALOG.md). A C# unit test
/// (<c>SessionEventPayloadsTests</c>) binds this mapping to <see cref="SessionEventTypes"/> in both directions
/// so every emitted event has exactly one payload contract here and no orphan record exists.
/// </para>
///
/// <para>
/// PAYLOAD IS IDENTIFIER-ONLY (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). Every record carries resource
/// IDENTIFIERS and generic state NAMES only — never resolved content, a display name or any other PII. The
/// command sites serialize these records with the default <c>System.Text.Json</c> options (no naming policy),
/// so the on-the-wire payload field names are the records' PascalCase property names; the TypeScript payload
/// contracts mirror those names exactly and the drift gate keeps the two in step.
/// </para>
/// </summary>
public static class SessionEventPayloads
{
    /// <summary>
    /// The payload of a <see cref="SessionEventTypes.SessionCreated"/>,
    /// <see cref="SessionEventTypes.SessionStarted"/> or <see cref="SessionEventTypes.SessionEnded"/> event:
    /// the session id and its lifecycle status NAME (e.g. <c>Prepared</c>/<c>Live</c>/<c>Ended</c>).
    /// Identifiers and a generic state name only — never any content (threat T7).
    /// </summary>
    public sealed record SessionLifecycleEventPayload(Guid SessionId, string Status);

    /// <summary>
    /// The payload of a <see cref="SessionEventTypes.ParticipantJoined"/> /
    /// <see cref="SessionEventTypes.ParticipantLeft"/> presence event: the surrogate id of the participant who
    /// joined or left. The participant id is an opaque surrogate — never a display name or any other PII
    /// (threat T7).
    /// </summary>
    public sealed record ParticipantPresenceEventPayload(Guid ParticipantId);

    /// <summary>
    /// The payload of a <see cref="SessionEventTypes.ContentRevealed"/> /
    /// <see cref="SessionEventTypes.ContentHidden"/> event: the generic kind and id of the revealed/hidden
    /// resource. Identifiers only — never the resolved content (threat T7).
    /// </summary>
    public sealed record ResourceReferenceEventPayload(string ResourceType, Guid ResourceId);

    /// <summary>
    /// The payload of a <see cref="SessionEventTypes.VisibilityRuleChanged"/> event: the changed resource's
    /// generic kind and id plus the new visibility STATE name (e.g. <c>Visible</c>/<c>Hidden</c>). Identifiers
    /// and a state name only — never resolved content (threat T7).
    /// </summary>
    public sealed record VisibilityRuleChangedEventPayload(string ResourceType, Guid ResourceId, string Visibility);

    /// <summary>
    /// The payload of a <see cref="SessionEventTypes.SceneActivated"/> event: the id of the activated scene.
    /// Identifier only — never resolved content (threat T7).
    /// </summary>
    public sealed record SceneActivatedEventPayload(Guid SceneId);

    /// <summary>
    /// The payload of a <see cref="SessionEventTypes.RecapGenerated"/> event: the recap id and the session it
    /// summarizes. Identifiers only — never the recap body (threat T7).
    /// </summary>
    public sealed record RecapGeneratedEventPayload(Guid RecapId, Guid SessionId);

    /// <summary>
    /// The payload CONTRACT for every emitted session event: each <see cref="SessionEventTypes"/> constant
    /// mapped to the record whose JSON the command site composes for it (CORE-RT-008). This is the single
    /// machine-readable binding of the emitted vocabulary to its payload shapes — the TypeScript drift gate
    /// parses it, and <c>SessionEventPayloadsTests</c> asserts its keys equal the
    /// <see cref="SessionEventTypes"/> constants in both directions, so every emitted event has exactly one
    /// payload contract and there is no orphan record. Ordinal keys, because event-type names are exact,
    /// case-sensitive Core identifiers.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Type> ByEventType = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [SessionEventTypes.SessionCreated] = typeof(SessionLifecycleEventPayload),
        [SessionEventTypes.SessionStarted] = typeof(SessionLifecycleEventPayload),
        [SessionEventTypes.SessionEnded] = typeof(SessionLifecycleEventPayload),
        [SessionEventTypes.ParticipantJoined] = typeof(ParticipantPresenceEventPayload),
        [SessionEventTypes.ParticipantLeft] = typeof(ParticipantPresenceEventPayload),
        [SessionEventTypes.SceneActivated] = typeof(SceneActivatedEventPayload),
        [SessionEventTypes.VisibilityRuleChanged] = typeof(VisibilityRuleChangedEventPayload),
        [SessionEventTypes.ContentRevealed] = typeof(ResourceReferenceEventPayload),
        [SessionEventTypes.ContentHidden] = typeof(ResourceReferenceEventPayload),
        [SessionEventTypes.RecapGenerated] = typeof(RecapGeneratedEventPayload),
    };
}
