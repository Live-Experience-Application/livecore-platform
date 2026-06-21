// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Participants;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Resolves the recipient USERS of a session event for the closed-app push fan-out (CORE-PUSH-002,
/// <see cref="ISessionEventPushRecipientResolver"/>). It produces the SAME audience the realtime
/// <see cref="SessionEventRecipientResolver"/> delivers to — gated through the SAME central Visibility engine
/// (<see cref="IEventRecipientVisibility"/>) — but expressed as the recipient participants' linked user ids, since
/// a push must address concrete subscriptions rather than a shared realtime group.
///
/// THE ROUTING MIRRORS <see cref="SessionEventRecipientResolver"/> exactly, so the push audience can never diverge
/// from the in-app audience (threats T2/T3):
/// <list type="bullet">
///   <item>A HOST-ONLY event (<see cref="SessionEventTypes.IsHostOnly"/>) has no participant audience — it reaches
///   the hosts only — so it produces NO push recipients (closed-app push targets the participant audience; a host
///   drives the session with the app open).</item>
///   <item>A SELECTED-participant event reaches ONLY its target participant, and only when the central engine says
///   that participant may see the subject (an audience-wide visible rule, or a rule scoped to exactly them); a
///   non-targeted or unauthorized participant is never a recipient (THE crown jewel, threat T3).</item>
///   <item>An AUDIENCE-WIDE event the whole audience may see (or one with no visibility subject) reaches every
///   active participant; an audience-wide event the audience-at-large may NOT see still reaches exactly the
///   participants made visible by a rule scoped to them — the SAME participant set the realtime resolver delivers
///   to.</item>
/// </list>
/// The audience population is the session's workspace's active participants
/// (<see cref="IParticipantRepository.ListActiveByWorkspaceAsync"/>, the same population the realtime audience and
/// the connection admission draw from). Anonymous participants (no user link) cannot be pushed and are omitted.
/// Visibility is NEVER recomputed here; an unrecognized/unauthorized subject fails closed to no recipients.
/// </summary>
internal sealed class SessionEventPushRecipientResolver : ISessionEventPushRecipientResolver
{
    private readonly IEventRecipientVisibility _visibility;
    private readonly IParticipantRepository _participants;

    public SessionEventPushRecipientResolver(
        IEventRecipientVisibility visibility,
        IParticipantRepository participants)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(participants);
        _visibility = visibility;
        _participants = participants;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ResolveRecipientUserIdsAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // A host-only event has no participant audience (it reaches the hosts only), so there is nothing to push.
        if (SessionEventTypes.IsHostOnly(sessionEvent.EventType))
        {
            return [];
        }

        // The session's audience population (its workspace's active participants), used both to enumerate an
        // audience-wide recipient set and to map any participant id to its linked user. ONE lookup per event — the
        // inherent cost of an outbound per-recipient fan-out (the realtime resolver avoids it via a shared group,
        // which push has no equivalent of).
        var participants = await _participants
            .ListActiveByWorkspaceAsync(sessionEvent.OrganizationId, sessionEvent.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        // The recipient PARTICIPANT ids, decided by the SAME routing the realtime resolver uses, then mapped to
        // linked users below. The audience decision is the central Visibility engine's (never recomputed here).
        var recipientParticipantIds = await ResolveRecipientParticipantIdsAsync(
                sessionEvent, participants, cancellationToken)
            .ConfigureAwait(false);
        if (recipientParticipantIds.Count == 0)
        {
            return [];
        }

        // Map recipient participants to their linked users (anonymous participants have no user and cannot be
        // pushed), DISTINCT so a user with several participant records is pushed once.
        var userIds = new HashSet<Guid>();
        foreach (var participant in participants)
        {
            if (participant.UserProfileId is { } userId && recipientParticipantIds.Contains(participant.Id))
            {
                userIds.Add(userId);
            }
        }

        return userIds.Count == 0 ? [] : userIds.ToArray();
    }

    /// <summary>
    /// Resolves the recipient PARTICIPANT ids for an event, mirroring
    /// <see cref="SessionEventRecipientResolver"/>'s routing and gating every audience decision through the central
    /// Visibility engine. Returns a set for O(1) membership tests when mapping to users.
    /// </summary>
    private async Task<HashSet<Guid>> ResolveRecipientParticipantIdsAsync(
        SessionEvent sessionEvent,
        IReadOnlyList<Participant> participants,
        CancellationToken cancellationToken)
    {
        // Resolve the audience visibility for this event ONLY when it has a subject (the central engine's
        // single-subject, session-scoped resolution). A no-subject event is unconditional (the whole audience).
        var audience = AudienceVisibility.None;
        if (sessionEvent.HasVisibilitySubject)
        {
            audience = await _visibility
                .ResolveAudienceRecipientsAsync(
                    sessionEvent.OrganizationId,
                    sessionEvent.WorkspaceId,
                    sessionEvent.SessionId,
                    sessionEvent.VisibilitySubjectType!,
                    sessionEvent.VisibilitySubjectId!.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // SELECTED / private: only the target participant, and only if the central engine authorizes them.
        if (sessionEvent.TargetParticipantId is { } selectedParticipantId)
        {
            var canReceive = !sessionEvent.HasVisibilitySubject
                || audience.AudienceVisible
                || audience.SelectedVisibleParticipantIds.Contains(selectedParticipantId);
            return canReceive ? [selectedParticipantId] : [];
        }

        // AUDIENCE-WIDE, unconditional (no subject) or the whole audience may see it: every active participant.
        if (!sessionEvent.HasVisibilitySubject || audience.AudienceVisible)
        {
            return participants.Select(participant => participant.Id).ToHashSet();
        }

        // AUDIENCE-WIDE but the audience-at-large may NOT see it: only the participants made visible by a rule
        // scoped to exactly them (the same per-participant set the realtime resolver delivers to).
        return audience.SelectedVisibleParticipantIds.ToHashSet();
    }
}
