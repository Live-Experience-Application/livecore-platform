using System.Security.Claims;
using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Fail-closed admission decision for a realtime hub connection (CORE-RT-001). The transport gate is the
/// hub's <c>[Authorize]</c> attribute, which rejects any connection without a validated OIDC bearer
/// token before the hub runs. This policy is the defence-in-depth check the hub itself applies on
/// connect: the authenticated principal must also normalize to a valid <see cref="OidcPrincipal"/>
/// through the same fail-closed <see cref="OidcPrincipalMapper"/> the REST endpoints use
/// (docs/02_ARCHITECTURE.md request flow; CORE-ID-001). A token that passes signature/lifetime/audience
/// validation but carries missing, conflicting or malformed identity claims is therefore still refused
/// at the hub — never a partially trusted connection (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// This story decides only WHETHER the connection is an authenticated, mappable principal. Resolving the
/// connection's organization/workspace/session context and participant identity, and joining the
/// server-managed groups, is CORE-RT-002; per-recipient event delivery is CORE-RT-003+. Keeping the
/// decision in this plain static class (not on the SignalR <c>Hub</c>) makes it directly unit-testable
/// without a hub or a live connection.
/// </summary>
internal static class RealtimeConnectionPolicy
{
    /// <summary>
    /// Whether the connection's authenticated principal is acceptable: it is non-null and normalizes to
    /// a valid <see cref="OidcPrincipal"/>. Returns <see langword="false"/> for a null principal or any
    /// principal the fail-closed mapper rejects, so the hub aborts the connection.
    /// </summary>
    public static bool IsAuthenticatedConnection(ClaimsPrincipal? user)
        => user is not null && OidcPrincipalMapper.Map(user).Succeeded;
}
