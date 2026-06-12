namespace LiveCore.Api.Realtime;

/// <summary>
/// Maps the Realtime module's SignalR hubs (CORE-RT-001), mirroring the <c>Map*Endpoints</c> pattern of
/// the REST modules. The session hub is mapped at the server-fixed
/// <see cref="RealtimeHubRoutes.SessionHub"/> path and requires authorization, so its negotiate and
/// connection endpoints challenge an unauthenticated client with 401. The hub's <c>[Authorize]</c>
/// attribute already enforces this; the explicit <c>RequireAuthorization()</c> here makes the
/// endpoint-level requirement unmistakable and independent of the attribute.
/// </summary>
internal static class RealtimeEndpoints
{
    public static IEndpointRouteBuilder MapRealtimeHubs(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapHub<SessionHub>(RealtimeHubRoutes.SessionHub)
            .RequireAuthorization();

        return endpoints;
    }
}
