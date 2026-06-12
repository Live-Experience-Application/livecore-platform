using LiveCore.Api.Organizations;

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
    public async Task<bool> CanAudienceReceiveAsync(
        Guid organizationId,
        Guid workspaceId,
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        // An unrecognized subject kind (or an empty id) can never be shown to the audience: fail closed.
        if (!TryParseSubjectType(subjectType, out var resourceType) || subjectId == Guid.Empty)
        {
            return false;
        }

        // Reuse the canonical CanViewResource decision under the AUDIENCE viewpoint (Participant), exactly
        // as the preview-as-participant query does, so the realtime audience gate equals the REST one.
        var decision = await _policy
            .CanViewResourceAsync(
                organizationId,
                workspaceId,
                MembershipRole.Participant,
                resourceType,
                subjectId,
                cancellationToken)
            .ConfigureAwait(false);

        return decision.CanView;
    }

    /// <inheritdoc />
    public async Task<bool> CanParticipantReceiveAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid participantId,
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        if (!TryParseSubjectType(subjectType, out var resourceType) || subjectId == Guid.Empty)
        {
            return false;
        }

        // Reuse the canonical per-participant decision (CORE-VIS-005): visible to THIS participant iff an
        // audience-wide visible rule, or a visible rule scoped to exactly them.
        return await _policy
            .CanParticipantViewResourceAsync(
                organizationId,
                workspaceId,
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
