// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The Visibility module's per-recipient event-visibility decision (CORE-RT-004) — the "recipient
/// calculation in Visibility module" control the threat model requires for realtime delivery (threat T3
/// in docs/07_SECURITY_THREAT_MODEL.md: "per-recipient event projection", "recipient calculation in
/// Visibility module"). It answers, for the resource a session event is ABOUT (the event's visibility
/// subject, named by a generic resource-kind string + id), whether a given realtime recipient may receive
/// the event.
///
/// It is the single seam the Realtime delivery (<c>SessionEventRecipientResolver</c>) uses to gate
/// recipients, so visibility is decided in exactly ONE place — the central Visibility engine — and the
/// realtime path can never diverge from the REST visibility decision (docs/05_MODULE_CONTRACTS.md: "Do
/// not duplicate visibility logic elsewhere"; the implementation REUSES <see cref="VisibilityPolicy"/>).
/// The resource KIND arrives as a string (the Realtime module stays decoupled from the
/// <see cref="VisibilityResourceType"/> enum); parsing it is this module's concern, and an
/// unrecognized/empty kind is treated as NOT visible — fail-closed, so a malformed subject can never leak
/// an event to the audience.
/// </summary>
internal interface IEventRecipientVisibility
{
    /// <summary>
    /// Resolves an AUDIENCE-WIDE event's recipients about the given subject resource from a SINGLE rule
    /// lookup (CORE-PERF-001): whether the WHOLE audience may see it (an audience-wide visible rule) AND the
    /// participants made visible only by a rule scoped to exactly them (a selected-participant reveal on the
    /// same resource), so the Realtime resolver gates the observers + shared session-audience group and any
    /// individually-entitled participant WITHOUT one query per participant. Replaces the old per-participant
    /// audience fan-out, so per-reveal DB load no longer grows with audience size. The lookup is tenant-,
    /// workspace- AND SESSION-scoped (CORE-SVIS-001): only the rules of the event's own session are
    /// consulted, so a reveal in a concurrent session of the same workspace can never make this event's
    /// subject visible here (the cross-session leak; threat T5/T3). An unrecognized/empty subject yields
    /// <see cref="AudienceVisibility.None"/> (fail-closed — no audience, no participants).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id, workspace id or session id is empty.</exception>
    Task<AudienceVisibility> ResolveAudienceRecipientsAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the audience recipients of MANY subjects from a SINGLE batched rule lookup (CORE-PERF-002) —
    /// the batch form of <see cref="ResolveAudienceRecipientsAsync"/> that reconnect replay uses to gate a
    /// whole slice of events without one query per event. Each subject is parsed fail-closed exactly like the
    /// single resolution (an unrecognized/empty subject maps to <see cref="AudienceVisibility.None"/>), and
    /// the recognized ones are resolved together through the canonical batched audience resolution
    /// (<see cref="VisibilityPolicy.ResolveAudienceVisibilityBatchAsync"/>), so the realtime audience gate
    /// equals the REST one and never diverges. The lookup is tenant-, workspace- AND SESSION-scoped
    /// (CORE-SVIS-001), so a reveal in a concurrent session of the same workspace never contributes (the
    /// cross-session leak; threat T5/T3). The returned map contains an entry for every DISTINCT requested
    /// subject; a subject absent from the map is treated as <see cref="AudienceVisibility.None"/> (fail-closed).
    /// </summary>
    /// <returns>A map from each requested (type-name, id) subject to its audience recipients.</returns>
    /// <exception cref="ArgumentException">The organization id, workspace id or session id is empty.</exception>
    Task<IReadOnlyDictionary<(string SubjectType, Guid SubjectId), AudienceVisibility>>
        ResolveAudienceRecipientsBatchAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            IReadOnlyCollection<(string SubjectType, Guid SubjectId)> subjects,
            CancellationToken cancellationToken);

    /// <summary>
    /// Whether the given SPECIFIC participant may receive an event about the given subject resource: true
    /// iff a visibility rule makes the resource visible to them (an audience-wide visible rule, or a
    /// visible rule scoped to exactly this participant). A rule scoped to a DIFFERENT participant does NOT
    /// grant it — the selected-participant guarantee, so a non-selected participant never receives a
    /// private event (threat T3/T5). The lookup is tenant-, workspace- AND SESSION-scoped (CORE-SVIS-001):
    /// only the event's own session's rules are consulted, so a reveal in a concurrent session never
    /// reaches them. An unrecognized subject kind yields <see langword="false"/> (fail-closed).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, session id or participant id is empty.
    /// </exception>
    Task<bool> CanParticipantReceiveAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);
}
