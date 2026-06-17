// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Maps the Realtime module's SignalR hubs (CORE-RT-001), mirroring the <c>Map*Endpoints</c> pattern of
/// the REST modules. The session hub is mapped at the server-fixed
/// <see cref="RealtimeHubRoutes.SessionHub"/> path and requires authorization, so its negotiate and
/// connection endpoints challenge an unauthenticated client with 401. The hub's <c>[Authorize]</c>
/// attribute already enforces this; the explicit <c>RequireAuthorization()</c> here makes the
/// endpoint-level requirement unmistakable and independent of the attribute.
///
/// The hub also pins the shared CORS allow-list policy (<see cref="CorsConfiguration.PolicyName"/>) with
/// <c>RequireCors</c> so a browser/PWA client may open the <c>/hubs</c> connection only from a configured
/// origin — the SAME allow-list the REST API applies through the pipeline-level <c>UseCors</c>
/// (CORE-OPS-003). It is layered on the authorization above, not a replacement for it.
/// </summary>
internal static class RealtimeEndpoints
{
    public static IEndpointRouteBuilder MapRealtimeHubs(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapHub<SessionHub>(RealtimeHubRoutes.SessionHub)
            .RequireAuthorization()
            .RequireCors(CorsConfiguration.PolicyName);

        return endpoints;
    }
}
