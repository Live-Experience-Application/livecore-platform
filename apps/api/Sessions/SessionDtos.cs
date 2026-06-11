namespace LiveCore.Api.Sessions;

/// <summary>
/// Response projection of a session (CORE-SES-004,
/// <c>POST /api/v1/sessions/{sessionId}/start</c> and
/// <c>POST /api/v1/sessions/{sessionId}/end</c>). It is the body returned by the
/// start/end lifecycle commands: the generic, product-neutral, server-side view
/// of the session AFTER the transition has been applied and persisted.
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md,
/// docs/08_API_CONTRACTS.md DTO design rules): identifiers, the tenant and
/// workspace boundaries, the display title, the lifecycle status and the
/// server timestamps only. It carries:
/// <list type="bullet">
///   <item>NO host-only or hidden fields and NO participant-only fields — the
///   session is a single generic resource, not split into host/participant
///   projections here, and it has no hidden content to leak (docs/08 DTO rules;
///   threat T7 in docs/07_SECURITY_THREAT_MODEL.md).</item>
///   <item>NO internal authorization rationale — it never echoes why the caller
///   was allowed or how the tenant/workspace was resolved (docs/08; threat
///   T7).</item>
///   <item>The <see cref="Status"/> as the stable enum NAME (Prepared/Live/Ended),
///   never the in-memory numeric discriminator, mirroring how the status is
///   persisted (<see cref="SessionConfiguration"/>) and how
///   <c>WorkspaceInvitationResponse</c> projects its role/status.</item>
/// </list>
///
/// Server timestamps are included per docs/08 ("Include server timestamps"):
/// <see cref="CreatedAt"/>/<see cref="UpdatedAt"/> are the row's audit
/// timestamps, and the nullable <see cref="StartedAt"/>/<see cref="EndedAt"/> are
/// the live-timeline boundaries (null until the session starts/ends), so a client
/// can see exactly which transition has occurred.
///
/// There is deliberately NO resource version/ETag field: the <see cref="Session"/>
/// aggregate carries no concurrency token yet (CORE-SES-002 did not add one), and
/// inventing one here would be speculative. docs/08 asks for a version "where
/// concurrent updates matter"; the state machine's invalid-transition guard
/// (409 on a re-start/re-end) already prevents the duplicate side effects two
/// racing start/end commands could otherwise cause, so optimistic concurrency on
/// the session row is noted as a follow-up rather than fabricated here.
/// </summary>
/// <param name="Id">Surrogate id of the session (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the session belongs to.</param>
/// <param name="WorkspaceId">Workspace the session belongs to.</param>
/// <param name="Title">Human-readable display title of the session.</param>
/// <param name="Status">
/// Lifecycle status name (Prepared/Live/Ended) after the applied transition.
/// </param>
/// <param name="StartedAt">
/// When the live timeline started (UTC), or <see langword="null"/> while the
/// session is still Prepared.
/// </param>
/// <param name="EndedAt">
/// When the live timeline ended (UTC), or <see langword="null"/> until the
/// session is Ended.
/// </param>
/// <param name="CreatedAt">When the session was created (UTC).</param>
/// <param name="UpdatedAt">When the session was last updated (UTC).</param>
public sealed record SessionResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    string Title,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="Session"/> aggregate into its response DTO. Only the
    /// generic, non-sensitive fields are copied; the status is emitted as its
    /// stable name.
    /// </summary>
    public static SessionResponse From(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionResponse(
            session.Id,
            session.OrganizationId,
            session.WorkspaceId,
            session.Title,
            session.Status.ToString(),
            session.StartedAt,
            session.EndedAt,
            session.CreatedAt,
            session.UpdatedAt);
    }
}
