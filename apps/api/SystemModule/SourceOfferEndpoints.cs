namespace LiveCore.Api.SystemModule;

/// <summary>
/// Endpoint wiring for the AGPL-3.0 section 13 source-offer surface (CORE-CMP-001).
/// It owns the anonymous <c>GET /source</c> route in one place so the host's
/// <c>Program.cs</c> maps it by intent (<see cref="MapLiveCoreSourceOfferEndpoint"/>),
/// mirroring how <c>MapLiveCoreHealthEndpoints</c> and <c>MapLiveCoreMetricsEndpoint</c>
/// own the other unauthenticated infrastructure routes.
///
/// The platform is AGPL-3.0-or-later and network-interactive (the SignalR hub plus
/// the <c>/api/v1</c> surface), so section 13 obliges it to OFFER remote users
/// access to the Corresponding Source of the version they interact with. The
/// endpoint is the surface that discharges that obligation: it returns the license,
/// the running build version and where the Corresponding Source can be obtained
/// (<see cref="SourceOffer"/>).
///
/// It is unauthenticated by design — the offer must reach any remote user
/// interacting with the application, exactly as the section 13 obligation is owed to
/// every such user — and it is a top-level infrastructure route, not part of the
/// versioned <c>/api/v1</c> product surface (so it is not a row in
/// <c>csv/api_routes.csv</c>, like <c>/health/*</c> and <c>/metrics</c>). It exposes
/// only the license, a build version and a public repository URL — no token, tenant
/// identifier, configuration value or resource content (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
internal static class SourceOfferEndpoints
{
    /// <summary>
    /// The route the source offer is served at.
    /// </summary>
    internal const string RoutePath = "/source";

    /// <summary>
    /// Maps the anonymous source-offer endpoint at <c>GET /source</c>, returning the
    /// AGPL section 13 source offer for the running build.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same endpoint route builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">The endpoint route builder is null.</exception>
    public static IEndpointRouteBuilder MapLiveCoreSourceOfferEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(RoutePath, (IConfiguration configuration) => Results.Ok(SourceOffer.ForRunningBuild(configuration)))
            .AllowAnonymous()
            .WithName("SourceOffer");

        return endpoints;
    }
}
