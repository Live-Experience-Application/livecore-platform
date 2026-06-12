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
    /// Whether the AUDIENCE (the audience roles — participants/observers — at large) may receive an event
    /// about the given subject resource: true iff a visibility rule makes the resource visible to the
    /// whole audience. Used to gate the observers group delivery of an audience-wide event. The lookup is
    /// tenant- and workspace-scoped (the organization boundary is checked before the workspace boundary;
    /// threat T5). An unrecognized subject kind yields <see langword="false"/> (fail-closed).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    Task<bool> CanAudienceReceiveAsync(
        Guid organizationId,
        Guid workspaceId,
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the given SPECIFIC participant may receive an event about the given subject resource: true
    /// iff a visibility rule makes the resource visible to them (an audience-wide visible rule, or a
    /// visible rule scoped to exactly this participant). A rule scoped to a DIFFERENT participant does NOT
    /// grant it — the selected-participant guarantee, so a non-selected participant never receives a
    /// private event (threat T3/T5). The lookup is tenant- and workspace-scoped. An unrecognized subject
    /// kind yields <see langword="false"/> (fail-closed).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or participant id is empty.
    /// </exception>
    Task<bool> CanParticipantReceiveAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid participantId,
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);
}
