using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.Realtime;

/// <summary>
/// The session realtime hub (CORE-RT-001, the first story of the Realtime Event Stream epic). The
/// Realtime module owns "connection tracking, hub groups, event delivery, reconnect replay" and "may
/// not send unfiltered events" (docs/05_MODULE_CONTRACTS.md); this story delivers ONLY the hub and its
/// AUTHENTICATION — the secure front door every later realtime capability connects through.
///
/// AUTHENTICATION. The hub is <c>[Authorize]</c>, so SignalR requires a validated OIDC bearer token to
/// negotiate and connect: an unauthenticated client is rejected (401) before any hub code runs, exactly
/// as the REST endpoints require authentication (docs/02_ARCHITECTURE.md). Because browser WebSocket
/// clients cannot send the <c>Authorization</c> header, the bearer token is also accepted from the
/// <c>access_token</c> query string for hub paths only (see <c>HubBearerToken</c> /
/// <c>OidcAuthenticationExtensions</c>); the token is validated by the same bearer pipeline either way.
/// On connect the hub additionally applies the fail-closed <see cref="RealtimeConnectionPolicy"/> and
/// ABORTS any connection whose principal does not normalize to a valid OIDC principal — defence in depth
/// so a token that is technically valid but carries malformed identity claims never holds an open
/// connection (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// DELIBERATELY EMPTY OTHERWISE. The hub exposes NO client-callable methods and joins NO groups yet: the
/// server-managed group model (<c>session:{id}:hosts</c>, <c>…:participant:{id}</c>, …,
/// docs/11_REALTIME_SYNC.md) is CORE-RT-002; event append and delivery (the deferred
/// SessionStarted/Ended and ContentRevealed/VisibilityRuleChanged events) are CORE-RT-003; per-recipient
/// projection, reconnect replay and scale-out are CORE-RT-004…006. No event is ever sent from here, so
/// this story cannot leak one (threat T3).
/// </summary>
[Authorize]
internal sealed class SessionHub : Hub
{
    /// <summary>
    /// Fail-closed admission on connect: abort any connection whose authenticated principal does not
    /// normalize to a valid OIDC principal (<see cref="RealtimeConnectionPolicy"/>). The <c>[Authorize]</c>
    /// gate has already rejected unauthenticated callers; this is the defence-in-depth check.
    /// </summary>
    public override Task OnConnectedAsync()
    {
        if (!RealtimeConnectionPolicy.IsAuthenticatedConnection(Context.User))
        {
            Context.Abort();
            return Task.CompletedTask;
        }

        return base.OnConnectedAsync();
    }
}
