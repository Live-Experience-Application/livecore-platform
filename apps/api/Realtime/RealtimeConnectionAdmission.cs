namespace LiveCore.Api.Realtime;

/// <summary>
/// Why a realtime hub connection was refused (CORE-RT-002). Used for server-side logging and tests; it
/// is NEVER sent to the client — a refused connection is simply aborted, so a probe cannot distinguish
/// the reasons and learn whether a tenant, session or participant exists (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). <see cref="None"/> means the connection was admitted.
/// </summary>
internal enum RealtimeConnectionDenialReason
{
    /// <summary>The connection was admitted; not a denial.</summary>
    None = 0,

    /// <summary>The connection carried no authenticated, mappable OIDC principal (fail-closed auth).</summary>
    InvalidPrincipal,

    /// <summary>
    /// The caller is not entitled to the target organization (claim/membership mismatch, unknown
    /// tenant, malformed slug) — the tenant boundary denied (threat T5).
    /// </summary>
    TenantDenied,

    /// <summary>The target session does not exist within the resolved tenant (hidden, threat T1/T5).</summary>
    SessionNotFound,

    /// <summary>
    /// The caller has no legitimate realtime relationship to the session: not a host/observer member of
    /// the session's workspace, and not the owner of an active participant in that workspace.
    /// </summary>
    NotAuthorizedForSession,
}

/// <summary>
/// The outcome of admitting a connection to the session hub (CORE-RT-002): either an admission carrying
/// the SERVER-COMPUTED groups the connection joins (and the authorized facts behind it), or a typed,
/// id-free denial. It is minted only through <see cref="Admit"/> / <see cref="Deny"/>, so an admission
/// always carries a non-empty group list and a <see cref="Subject"/>, and a denial never carries any group,
/// subject or id (a denial leaks nothing about what exists; threats T1/T5). This mirrors the fail-closed
/// result shape of <c>SessionJoinResult</c> (CORE-SES-003).
/// </summary>
internal sealed class RealtimeConnectionAdmission
{
    private static readonly IReadOnlyList<string> _noGroups = [];

    private RealtimeConnectionAdmission(
        bool admitted,
        IReadOnlyList<string> groups,
        RealtimeConnectionSubject? subject,
        RealtimeConnectionDenialReason reason)
    {
        Admitted = admitted;
        Groups = groups;
        Subject = subject;
        Reason = reason;
    }

    /// <summary>Whether the connection is admitted (and should join <see cref="Groups"/>).</summary>
    public bool Admitted { get; }

    /// <summary>
    /// The server-computed groups the admitted connection joins (never empty when admitted; always empty
    /// on a denial). The client never supplies these — they are derived from the caller's authorized
    /// role/identity.
    /// </summary>
    public IReadOnlyList<string> Groups { get; }

    /// <summary>
    /// The authorized facts of the admitted connection (tenant/workspace/session, the subject behind it,
    /// and the participant it owns for a participant connection); the hub records these so a later
    /// re-authorization (CORE-RTC-002) can find and evict the connection. Non-null exactly when
    /// <see cref="Admitted"/> is true.
    /// </summary>
    public RealtimeConnectionSubject? Subject { get; }

    /// <summary>The denial reason (<see cref="RealtimeConnectionDenialReason.None"/> when admitted).</summary>
    public RealtimeConnectionDenialReason Reason { get; }

    /// <summary>
    /// Builds an admission for the given non-empty set of server-computed groups and the authorized facts
    /// behind the connection.
    /// </summary>
    /// <exception cref="ArgumentException">The group list is null or empty.</exception>
    public static RealtimeConnectionAdmission Admit(IReadOnlyList<string> groups, RealtimeConnectionSubject subject)
    {
        if (groups is null || groups.Count == 0)
        {
            throw new ArgumentException("An admitted connection must join at least one group.", nameof(groups));
        }

        return new RealtimeConnectionAdmission(admitted: true, groups, subject, RealtimeConnectionDenialReason.None);
    }

    /// <summary>Builds a fail-closed denial carrying no groups, no subject and no ids.</summary>
    /// <exception cref="ArgumentException">The reason is <see cref="RealtimeConnectionDenialReason.None"/>.</exception>
    public static RealtimeConnectionAdmission Deny(RealtimeConnectionDenialReason reason)
    {
        if (reason == RealtimeConnectionDenialReason.None)
        {
            throw new ArgumentException("A denial must carry a reason.", nameof(reason));
        }

        return new RealtimeConnectionAdmission(admitted: false, _noGroups, subject: null, reason);
    }
}
