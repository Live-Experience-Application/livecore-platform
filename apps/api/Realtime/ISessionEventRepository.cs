namespace LiveCore.Api.Realtime;

/// <summary>
/// Persistence contract for the append-only session event stream (CORE-RT-003). The Realtime module owns
/// the <c>session_events</c> table; other modules cause events to be appended only through the Realtime
/// publisher, never by writing this table directly (docs/02_ARCHITECTURE.md: no direct table ownership
/// violations; docs/05_MODULE_CONTRACTS.md: the Realtime module owns "event delivery").
///
/// APPEND-ONLY. The contract exposes only an append and a tenant- and session-scoped read: there is NO
/// update and NO delete, because session events are immutable (docs/10_DATABASE_SCHEMA.md: "session
/// events are append-only"). Every read is scoped by BOTH the organization and the session, leading with
/// the tenant column, so one tenant's or one session's events are never returned through another's id
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1). There is no list-everything method and no
/// by-id-alone lookup.
/// </summary>
public interface ISessionEventRepository
{
    /// <summary>
    /// Appends an event to its session's stream. The append allocates and stamps the event's per-session,
    /// gap-free, strictly monotonic <see cref="SessionEvent.Sequence"/> (CORE-RTC-001) before persisting it,
    /// so ordering and replay use the sequence rather than the millisecond-resolution id. The event is
    /// immutable; this is the only write path. A foreign-key violation (a non-existent
    /// tenant/workspace/session) surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every event of the given session (owned by the given organization) in deterministic append
    /// order — by the per-session, gap-free, strictly monotonic <see cref="SessionEvent.Sequence"/>
    /// (CORE-RTC-001), which preserves the append order of events created within the same millisecond
    /// (unlike the millisecond-resolution UUIDv7 id) and is provider-independent. The list is tenant- AND
    /// session-scoped: the predicate leads with <c>organization_id</c> and matches <c>session_id</c>, so a
    /// foreign tenant's or session's events are NEVER returned even when their ids would otherwise be
    /// addressable (threat T5/T1). The <c>created_at</c> column still backs the time-range replay index
    /// (CORE-RT-005).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or session id is empty.</exception>
    Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
        Guid organizationId,
        Guid sessionId,
        CancellationToken cancellationToken);
}
