// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.Json;
using LiveCore.Api.Realtime;

namespace LiveCore.Api.Visibility;

/// <summary>
/// Composes the durable realtime SESSION EVENTS a reveal emits when it ACTUALLY changes a resource's audience
/// visibility (CORE-VIS-004 / CORE-EVT-003). It is the SINGLE place those event shapes are built, so the two
/// callers that drive the central reveal command — the live host reveal endpoint
/// (<see cref="RevealEndpoints"/>) and the worker's scheduled auto-reveal
/// (<see cref="ScheduledRevealService"/>, CORE-VSEAL-002) — emit the SAME events and can never diverge: the
/// scheduled auto-reveal is not a duplicated reveal path, it drives the same engine and emits the same events.
///
/// THE EVENTS (each CONCERNS THE GOVERNED RESOURCE, so it carries the revealed resource as its VISIBILITY
/// SUBJECT and the recipient resolver gates delivery through the central Visibility engine — the hosts always
/// receive it and the audience only when they may see the resource, so no recipient ever gets an event about a
/// resource they may not see; threats T2/T3 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item><c>ContentRevealed</c> (CORE-RT-003) — the central participant-facing reveal event.</item>
///   <item><c>VisibilityRuleChanged</c> (CORE-EVT-003) — the realtime counterpart of the audit record, the new
///   state being <see cref="VisibilityState.Visible"/>.</item>
///   <item><c>SceneActivated</c> (CORE-EVT-003) — additionally, when the revealed resource is a Scene (revealing
///   a Scene IS the documented scene switch).</item>
/// </list>
///
/// The events carry resource IDENTIFIERS only, never resolved content (threat T7). The <c>actorUserProfileId</c>
/// is the <c>createdBy</c>: the authenticated host for a live reveal, or <see langword="null"/> for the worker's
/// SYSTEM scheduled auto-reveal (CORE-VSEAL-002) — exactly as the recap/retention background jobs emit
/// system events with no actor. This method ONLY builds the events; the caller APPENDS them (inside its unit of
/// work) and DELIVERS them (the endpoint delivers live after commit; the worker relies on the recipient-gated
/// reconnect replay, the established background-job pattern), so the durable-vs-delivery seam stays the caller's.
/// </summary>
internal static class RevealSessionEvents
{
    /// <summary>
    /// Builds the durable session events a reveal of the given resource emits (see the type summary). The list
    /// is ordered ContentRevealed, VisibilityRuleChanged, then SceneActivated (for a Scene), matching the live
    /// reveal endpoint's existing emission order so replay and tests observe an identical stream. The caller
    /// invokes this ONLY on a real visibility change.
    /// </summary>
    /// <param name="organizationId">The tenant the reveal happened in.</param>
    /// <param name="workspaceId">The session's workspace the resource belongs to.</param>
    /// <param name="sessionId">The session the reveal happened in (the reveal is session-scoped, CORE-SVIS-001).</param>
    /// <param name="actorUserProfileId">
    /// The host who performed the reveal, or <see langword="null"/> for the worker's system scheduled auto-reveal.
    /// </param>
    /// <param name="targetParticipantId">
    /// The selected participant for a private reveal, or <see langword="null"/> for an audience-wide reveal.
    /// </param>
    /// <param name="resourceType">The kind of resource that was revealed.</param>
    /// <param name="resourceId">The surrogate id of the resource that was revealed.</param>
    /// <param name="now">The reveal timestamp (the events' created time).</param>
    public static IReadOnlyList<SessionEvent> Compose(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid? actorUserProfileId,
        Guid? targetParticipantId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        DateTimeOffset now)
    {
        var resourceTypeName = resourceType.ToString();
        var events = new List<SessionEvent>(capacity: 3);

        // CONTENT-REVEALED (CORE-RT-003): the central participant-facing reveal event. Identifier-only payload
        // (threat T7/T2); the revealed resource is the event's VISIBILITY SUBJECT (CORE-RT-004) so the recipient
        // resolver projects per-recipient through the Visibility engine — a non-selected participant is not in
        // the target group and cannot receive it (threat T3).
        var contentRevealedPayload = JsonSerializer.Serialize(
            new SessionEventPayloads.ResourceReferenceEventPayload(resourceTypeName, resourceId));
        events.Add(SessionEvent.Create(
            organizationId,
            workspaceId,
            sessionId,
            SessionEventTypes.ContentRevealed,
            actorUserProfileId,
            targetParticipantId,
            contentRevealedPayload,
            schemaVersion: 1,
            now,
            visibilitySubjectType: resourceTypeName,
            visibilitySubjectId: resourceId));

        // VISIBILITY-RULE-CHANGED (CORE-EVT-003): the rule's new state is Visible — the realtime counterpart of
        // the audit record, gated through the central engine by its visibility subject (threats T2/T3).
        var ruleChangedPayload = JsonSerializer.Serialize(
            new SessionEventPayloads.VisibilityRuleChangedEventPayload(
                resourceTypeName,
                resourceId,
                VisibilityState.Visible.ToString()));
        events.Add(SessionEvent.Create(
            organizationId,
            workspaceId,
            sessionId,
            SessionEventTypes.VisibilityRuleChanged,
            actorUserProfileId,
            targetParticipantId,
            ruleChangedPayload,
            schemaVersion: 1,
            now,
            visibilitySubjectType: resourceTypeName,
            visibilitySubjectId: resourceId));

        // SCENE-ACTIVATED (CORE-EVT-003): revealing a Scene IS the documented scene switch, so a Scene reveal
        // additionally appends SceneActivated, gated by the activated scene's visibility subject.
        if (resourceType == VisibilityResourceType.Scene)
        {
            var sceneActivatedPayload = JsonSerializer.Serialize(
                new SessionEventPayloads.SceneActivatedEventPayload(resourceId));
            events.Add(SessionEvent.Create(
                organizationId,
                workspaceId,
                sessionId,
                SessionEventTypes.SceneActivated,
                actorUserProfileId,
                targetParticipantId,
                sceneActivatedPayload,
                schemaVersion: 1,
                now,
                visibilitySubjectType: resourceTypeName,
                visibilitySubjectId: resourceId));
        }

        return events;
    }
}
