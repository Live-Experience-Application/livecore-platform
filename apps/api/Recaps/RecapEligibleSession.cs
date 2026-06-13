namespace LiveCore.Api.Recaps;

/// <summary>
/// The minimal, generic coordinates of a session that is ELIGIBLE for an automatic recap (CORE-JOB-001): a
/// session that has ENDED and has no recap yet. It is the read-only projection
/// <see cref="IRecapEligibleSessionReader"/> returns to <see cref="RecapGenerationService"/>, carrying only
/// what the service needs to produce a <see cref="Recap"/> for that session — the tenant, workspace and
/// session boundary ids (so the produced recap is scoped to exactly that tenant/workspace/session, threat T5
/// in docs/07_SECURITY_THREAT_MODEL.md) and the live-timeline boundaries the generic system summary is
/// composed from.
///
/// It is a projection, NOT the <see cref="LiveCore.Api.Sessions.Session"/> aggregate: the Recaps module reads
/// only the session columns it needs to decide eligibility and compose a recap, never the session's display
/// title or any free-form metadata (so no session content can leak into a recap or a log; threat T7). An ended
/// session always has both live-timeline timestamps (it can only end from a live, started session), but both
/// are modeled as nullable to mirror the underlying nullable columns and so a defensive composer never assumes
/// they are present.
/// </summary>
/// <param name="OrganizationId">Tenant the session (and therefore its recap) belongs to.</param>
/// <param name="WorkspaceId">Workspace the session (and therefore its recap) belongs to.</param>
/// <param name="SessionId">The ended session that needs a recap.</param>
/// <param name="StartedAt">When the session's live timeline started (UTC), if recorded.</param>
/// <param name="EndedAt">When the session's live timeline ended (UTC), if recorded.</param>
public readonly record struct RecapEligibleSession(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid SessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);
