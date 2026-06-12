namespace LiveCore.Api.Realtime;

/// <summary>
/// The canonical, SERVER-OWNED names of the realtime hub groups (CORE-RT-002). This is the single place
/// group names are formed; every group a connection joins comes from one of these builders, so a client
/// can never choose an arbitrary group name (docs/11_REALTIME_SYNC.md: "Do not let clients choose
/// arbitrary group names" — the connection supplies IDENTIFIERS, the server computes the NAMES). The
/// names match the documented connection model exactly:
/// <code>
/// org:{organizationId}
/// workspace:{workspaceId}:hosts
/// session:{sessionId}:hosts
/// session:{sessionId}:participant:{participantId}
/// session:{sessionId}:observers
/// </code>
///
/// Each builder rejects an empty id: an empty id can never identify a real org/workspace/session/
/// participant, so forming a group name from one would create a meaningless, potentially overlapping
/// group — fail fast instead (threat T3 in docs/07_SECURITY_THREAT_MODEL.md: realtime delivery must
/// never reach the wrong recipient). Ids are formatted by their stable string form; the names are
/// server-internal addressing, never shown to a client.
/// </summary>
internal static class RealtimeGroups
{
    /// <summary>The org-wide group <c>org:{organizationId}</c> — host-capable staff connections.</summary>
    public static string Organization(Guid organizationId)
        => $"org:{Require(organizationId, nameof(organizationId))}";

    /// <summary>The workspace hosts group <c>workspace:{workspaceId}:hosts</c>.</summary>
    public static string WorkspaceHosts(Guid workspaceId)
        => $"workspace:{Require(workspaceId, nameof(workspaceId))}:hosts";

    /// <summary>The session hosts group <c>session:{sessionId}:hosts</c>.</summary>
    public static string SessionHosts(Guid sessionId)
        => $"session:{Require(sessionId, nameof(sessionId))}:hosts";

    /// <summary>
    /// The single-participant group <c>session:{sessionId}:participant:{participantId}</c>. A
    /// participant connection joins ONLY its own such group, so a private (selected-participant) event
    /// can be addressed to exactly one participant and a non-selected participant can never receive it
    /// (threat T3).
    /// </summary>
    public static string SessionParticipant(Guid sessionId, Guid participantId)
        => $"session:{Require(sessionId, nameof(sessionId))}:participant:{Require(participantId, nameof(participantId))}";

    /// <summary>The session observers group <c>session:{sessionId}:observers</c>.</summary>
    public static string SessionObservers(Guid sessionId)
        => $"session:{Require(sessionId, nameof(sessionId))}:observers";

    private static Guid Require(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty when forming a realtime group name.", name);
        }

        return id;
    }
}
