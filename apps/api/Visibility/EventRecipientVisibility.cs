// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The Visibility module's per-recipient event-visibility decision (CORE-RT-004,
/// <see cref="IEventRecipientVisibility"/>). It is a thin, fail-closed adapter over the canonical
/// <see cref="VisibilityPolicy"/> (CORE-VIS-002): it parses the event's subject resource KIND (a string,
/// so the Realtime module stays decoupled from the <see cref="VisibilityResourceType"/> enum) and
/// DELEGATES the actual visibility decision to the policy, so the realtime recipient calculation and the
/// REST visibility decision are the SAME decision and can never diverge (docs/05_MODULE_CONTRACTS.md: the
/// Visibility module is the central security module and visibility logic must not be "duplicate[d]
/// elsewhere"; threat T3's "recipient calculation in Visibility module").
///
/// FAIL-CLOSED on the subject kind: the kind is parsed from its NAME only (case-sensitive, never by
/// number — a client must not smuggle in an undefined enum value), exactly like the reveal endpoint's
/// resource-type parsing. An unrecognized or numeric kind can never identify a governed resource, so it
/// is treated as NOT visible — a malformed subject never leaks an event to a recipient (threats T1/T3).
/// </summary>
internal sealed class EventRecipientVisibility : IEventRecipientVisibility
{
    private readonly VisibilityPolicy _policy;

    public EventRecipientVisibility(VisibilityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <inheritdoc />
    public async Task<AudienceVisibility> ResolveAudienceRecipientsAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        // An unrecognized subject kind (or an empty id) can never be shown to the audience: fail closed
        // (no audience, no individually-entitled participant).
        if (!TryParseSubjectType(subjectType, out var resourceType) || subjectId == Guid.Empty)
        {
            return AudienceVisibility.None;
        }

        // Reuse the canonical SESSION-SCOPED audience resolution (CORE-PERF-001): ONE rule lookup yields
        // both the audience-wide decision and the participant-scoped visible set, bounded by the event's
        // own session (CORE-SVIS-001), so the realtime audience gate equals the REST one and a reveal in a
        // concurrent session of the same workspace can never make this event's subject visible here.
        return await _policy
            .ResolveAudienceVisibilityAsync(
                organizationId,
                workspaceId,
                sessionId,
                resourceType,
                subjectId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<(string SubjectType, Guid SubjectId), AudienceVisibility>>
        ResolveAudienceRecipientsBatchAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            IReadOnlyCollection<(string SubjectType, Guid SubjectId)> subjects,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        var result = new Dictionary<(string SubjectType, Guid SubjectId), AudienceVisibility>();
        if (subjects.Count == 0)
        {
            return result;
        }

        // Parse each DISTINCT subject's kind fail-closed (by NAME only, never numeric — the same parse the
        // single resolution uses). A recognized subject maps to a resource ref; an unrecognized or empty one
        // is mapped straight to None (fail-closed — a malformed subject never leaks an event).
        var refBySubject = new Dictionary<(string SubjectType, Guid SubjectId), (VisibilityResourceType ResourceType, Guid ResourceId)>();
        foreach (var subject in subjects.Distinct())
        {
            if (TryParseSubjectType(subject.SubjectType, out var resourceType) && subject.SubjectId != Guid.Empty)
            {
                refBySubject[subject] = (resourceType, subject.SubjectId);
            }
            else
            {
                result[subject] = AudienceVisibility.None;
            }
        }

        if (refBySubject.Count == 0)
        {
            return result;
        }

        // Resolve every recognized subject through the canonical SESSION-SCOPED batched audience resolution
        // (CORE-PERF-002 + CORE-PERF-001): ONE batched rule lookup yields each subject's audience-wide
        // decision and participant-scoped visible set, so the realtime audience gate equals the REST one and
        // a reveal in a concurrent session of the same workspace can never contribute here.
        var resolved = await _policy
            .ResolveAudienceVisibilityBatchAsync(
                organizationId,
                workspaceId,
                sessionId,
                refBySubject.Values.Distinct().ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var (subject, resourceRef) in refBySubject)
        {
            result[subject] = resolved.TryGetValue(resourceRef, out var audience)
                ? audience
                : AudienceVisibility.None;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> CanParticipantReceiveAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        if (!TryParseSubjectType(subjectType, out var resourceType) || subjectId == Guid.Empty)
        {
            return false;
        }

        // Reuse the canonical SESSION-SCOPED per-participant decision (CORE-VIS-005 + CORE-SVIS-001):
        // visible to THIS participant iff an audience-wide visible rule, or a visible rule scoped to
        // exactly them, exists IN THE EVENT'S OWN SESSION — so a reveal in a concurrent session never
        // reaches them.
        return await _policy
            .CanParticipantViewResourceAsync(
                organizationId,
                workspaceId,
                sessionId,
                participantId,
                resourceType,
                subjectId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a <see cref="VisibilityResourceType"/> from its NAME only, rejecting null/blank, numeric
    /// values and unknown names — so a malformed subject kind can never bind to an enum member (mirrors
    /// the reveal endpoint's resource-type parsing).
    /// </summary>
    private static bool TryParseSubjectType(string? value, out VisibilityResourceType resourceType)
    {
        resourceType = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (int.TryParse(value, out _))
        {
            return false;
        }

        return Enum.TryParse(value, ignoreCase: false, out resourceType)
            && VisibilityRule.IsValidResourceType(resourceType);
    }
}
