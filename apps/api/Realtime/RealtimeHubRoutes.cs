using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Server-owned route paths for the Realtime module's SignalR hubs (CORE-RT-001). The hub paths live
/// under the shared <see cref="HubBearerToken.PathPrefix"/> (<c>/hubs</c>) area so the JWT bearer
/// handler's query-string-token rule (which is restricted to that area) applies to them and to no REST
/// route. Paths are fixed by the server — clients never choose them (docs/11_REALTIME_SYNC.md: "Do not
/// let clients choose arbitrary group names"; the same principle applies to hub routes).
/// </summary>
internal static class RealtimeHubRoutes
{
    /// <summary>
    /// The session realtime hub: <c>/hubs/session</c>. The single hub for live session connections; the
    /// server-managed groups, event delivery and reconnect replay it will carry are later Realtime
    /// stories (CORE-RT-002…006).
    /// </summary>
    public const string SessionHub = HubBearerToken.PathPrefix + "/session";
}
