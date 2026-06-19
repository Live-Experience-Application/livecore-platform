// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>Which kind of connection a cross-instance eviction targets (CORE-RES-008).</summary>
internal enum RealtimeConnectionEvictionKind
{
    /// <summary>A participant connection, matched by tenant/workspace/session/participant.</summary>
    Participant,

    /// <summary>A workspace member (host/observer) connection, matched by tenant/workspace/subject.</summary>
    WorkspaceMember,
}

/// <summary>
/// The opaque descriptor a connection eviction is broadcast as across API replicas (CORE-RES-008). It is the wire
/// form the <see cref="RealtimeConnectionRegistry"/> publishes over the
/// <see cref="IRealtimeConnectionEvictionBackplane"/> and that every replica's
/// <see cref="RealtimeConnectionEvictionListener"/> parses to abort the matching connections it holds. It mirrors the
/// authorization-cache invalidation token (CORE-RES-007): a compact, prefix-tagged string of "N"-format surrogate
/// Guids — the tenant/workspace/session and the participant or subject id — and NOTHING else (no display name,
/// token or content; threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The values are generic identifiers only
/// (AGENTS.md).
///
/// <para>
/// <see cref="SubjectId"/> is the PARTICIPANT id for a <see cref="RealtimeConnectionEvictionKind.Participant"/>
/// eviction and the user-profile SUBJECT id for a <see cref="RealtimeConnectionEvictionKind.WorkspaceMember"/>
/// eviction (which does not use <see cref="SessionId"/>), exactly matching the two
/// <see cref="IRealtimeConnectionEvictor"/> methods.
/// </para>
/// </summary>
internal readonly record struct RealtimeConnectionEviction(
    RealtimeConnectionEvictionKind Kind,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid SessionId,
    Guid SubjectId)
{
    // Single-character kind prefixes keep the token compact and distinct from the authorization-cache invalidation
    // token shapes ("s:"/"o:") so a stray message on a shared channel can never be mistaken for the other feature's.
    private const string _participantPrefix = "p";
    private const string _workspaceMemberPrefix = "m";

    /// <summary>Builds the descriptor for evicting a participant's connections (the <see cref="IRealtimeConnectionEvictor.EvictParticipantAsync"/> match).</summary>
    public static RealtimeConnectionEviction ForParticipant(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId)
        => new(RealtimeConnectionEvictionKind.Participant, organizationId, workspaceId, sessionId, participantId);

    /// <summary>Builds the descriptor for evicting a workspace member's connections (the <see cref="IRealtimeConnectionEvictor.EvictWorkspaceMemberAsync"/> match).</summary>
    public static RealtimeConnectionEviction ForWorkspaceMember(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId)
        => new(RealtimeConnectionEvictionKind.WorkspaceMember, organizationId, workspaceId, Guid.Empty, userProfileId);

    /// <summary>
    /// Serializes the descriptor to its opaque wire token. A participant token is
    /// <c>p:&lt;org&gt;:&lt;workspace&gt;:&lt;session&gt;:&lt;participant&gt;</c>; a member token is
    /// <c>m:&lt;org&gt;:&lt;workspace&gt;:&lt;subject&gt;</c> — "N"-format Guids only.
    /// </summary>
    public string Serialize() => Kind switch
    {
        RealtimeConnectionEvictionKind.Participant => string.Join(
            ':',
            _participantPrefix,
            OrganizationId.ToString("N"),
            WorkspaceId.ToString("N"),
            SessionId.ToString("N"),
            SubjectId.ToString("N")),
        RealtimeConnectionEvictionKind.WorkspaceMember => string.Join(
            ':',
            _workspaceMemberPrefix,
            OrganizationId.ToString("N"),
            WorkspaceId.ToString("N"),
            SubjectId.ToString("N")),
        _ => throw new InvalidOperationException($"Unknown eviction kind '{Kind}'."),
    };

    /// <summary>
    /// Parses an eviction token a peer replica published. It validates the shape DEFENSIVELY — the kind prefix, the
    /// exact segment count and that every segment is an "N"-format Guid — so a malformed or unexpected message on the
    /// shared backplane channel is rejected and can never evict an unintended connection (the cross-instance
    /// counterpart of <c>AuthorizationLookupCache.IsInvalidationGroup</c>). Returns <see langword="false"/> for a
    /// null/empty/malformed token.
    /// </summary>
    public static bool TryParse(string? token, out RealtimeConnectionEviction eviction)
    {
        eviction = default;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var parts = token.Split(':');
        switch (parts[0])
        {
            case _participantPrefix when parts.Length == 5:
                if (TryGuid(parts[1], out var participantOrg)
                    && TryGuid(parts[2], out var participantWorkspace)
                    && TryGuid(parts[3], out var session)
                    && TryGuid(parts[4], out var participantId))
                {
                    eviction = ForParticipant(participantOrg, participantWorkspace, session, participantId);
                    return true;
                }

                return false;

            case _workspaceMemberPrefix when parts.Length == 4:
                if (TryGuid(parts[1], out var memberOrg)
                    && TryGuid(parts[2], out var memberWorkspace)
                    && TryGuid(parts[3], out var subjectId))
                {
                    eviction = ForWorkspaceMember(memberOrg, memberWorkspace, subjectId);
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool TryGuid(string value, out Guid result) => Guid.TryParseExact(value, "N", out result);
}
