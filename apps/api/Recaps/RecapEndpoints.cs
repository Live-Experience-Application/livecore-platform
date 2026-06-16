using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Recaps;

/// <summary>
/// HTTP endpoint of the Recaps module's recap read (CORE-RCP-003, the "Vertical Authoring and Read API
/// Completeness" epic: "Add the recap read endpoint"). Until now a recap had NO HTTP surface — the aggregate
/// (<see cref="Recap"/>, CORE-AUD-004), its tenant-scoped persistence (<see cref="IRecapRepository"/>), its EF
/// migration and the host-vs-audience <see cref="RecapProjection"/> existed and were unit-tested, and the
/// background worker produced a system recap for every ended session (CORE-RCP-001/002), but no route was
/// mapped (csv/api_routes.csv defined none, recorded as a deliberate Core-later deferral in
/// docs/24_SPEC_CONSISTENCY.md, CORE-SPEC-003). An end user therefore could not retrieve a generated recap.
/// This story adds that route ON TOP of the existing repository + projection, without a parallel recap engine.
///
/// Route owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/sessions/{sessionId}/recap</c> — module <b>Recaps</b>, roles "workspace members", a
///   tenant/workspace/session-scoped read of a session's generated recap, ROLE-PROJECTED. The route path
///   carries only the <c>{sessionId}</c>, so the target tenant is the required <c>?organizationSlug=</c> query
///   parameter exactly like the by-session-id lifecycle commands and the reconnect-replay route.</item>
/// </list>
///
/// WHICH RECAP. A session may accumulate more than one recap over time — the worker produces at most one
/// SYSTEM recap per session (CORE-RCP-001), and a host may later produce additional host recaps through the
/// same repository — so this singular route returns the MOST RECENTLY produced recap for the session (the last
/// in <see cref="IRecapRepository.ListBySessionAsync"/>'s produced order, the time-ordered UUIDv7 surrogate
/// id). Reusing the existing tenant- and workspace-scoped list read keeps this endpoint on the same fail-closed
/// repository the worker uses, rather than adding a parallel "find one" lookup; today, where only the system
/// recap exists, that is exactly the generated recap. A session with no recap yet is a hidden 404, not an empty
/// body.
///
/// AUTHORIZATION (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5/T8), authorized
/// exactly like the session read surface (the scene by-id and session list routes: any "workspace members"
/// role may read, and the member's actual workspace ROLE then drives the projection), load-then-authorize,
/// fail-closed at every step and never leaking why:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="System.Security.Claims.ClaimsPrincipal"/>;
///   a failed mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed session id and a denied tenant
///   resolution (a service account, a foreign/unclaimed/unknown tenant) are hidden as 404 — a caller who
///   cannot see the tenant must not learn whether the session or its recap exists (threat T5).</item>
///   <item>The session is loaded WITHIN the resolved tenant (<see cref="ISessionRepository.FindByIdInOrganizationAsync"/>,
///   leading with the organization id), so a session in another tenant is never returned even when the
///   surrogate id matches; a cross-tenant or unknown session is hidden as 404 (threats T1/T5). The session's
///   own workspace id is discovered from the loaded row AFTER the tenant boundary is enforced.</item>
///   <item>A caller who is not a member of the session's workspace is ALSO hidden as 404 (a non-member must not
///   learn the session — or its recap — exists; the same object-level rule as the scene by-id and session list
///   routes), never 403.</item>
///   <item>A known workspace member receives the recap PROJECTED BY ROLE through the SAME
///   <see cref="RecapProjection"/> the worker journey exercises (CORE-E2E-003): the host-content and
///   metadata roles (Owner/Admin/Host/CoHost/Auditor) receive the full <see cref="RecapView"/> WITH the recap
///   body; the audience roles (Participant/Observer, and any undefined value) fall closed to the host-only-field
///   -stripped <see cref="RecapSummaryView"/> WITHOUT the body. The recap body is host content, never returned
///   to the audience until a separate reveal (docs/09_EVENT_CATALOG.md <c>RecapGenerated</c>:
///   "Participant-visible only after separate reveal"; threats T2/T8). <see cref="MembershipRole"/> is
///   non-linear, so the classification is EXACT set membership, never a &gt;/&lt; comparison.</item>
/// </list>
///
/// There is deliberately no 403 path here: the read is allowed to ANY workspace member, and the only role
/// effect is the SHAPE of the response (host vs audience), so an unauthorized role does not lose access — it
/// loses the host-only fields. A caller with no standing at all is hidden as 404 (threats T1/T5/T8). The
/// separate participant REVEAL of a recap body remains a later story (docs/24_SPEC_CONSISTENCY.md).
///
/// Persistence dependency: like the session/audit endpoints, this uses the tenant context resolver and the
/// session, workspace-member and recap repositories, registered only when a database connection string is
/// configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class RecapEndpoints
{
    /// <summary>Required query parameter naming the target organization (the tenant the session belongs to).</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapRecapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token
        // is challenged as 401 before any handler runs. The session-scoped recap read shares the by-session-id
        // prefix with the lifecycle commands; the full template differs, so the modules can share the prefix
        // without collision (the same way the scene routes share the workspaces prefix).
        var bySession = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        bySession.MapGet("/{sessionId}/recap", ReadRecapAsync);

        return endpoints;
    }

    // GET /api/v1/sessions/{sessionId}/recap?organizationSlug={slug}
    private static async Task<IResult> ReadRecapAsync(
        HttpContext httpContext,
        string sessionId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable();
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        // The target organization is required and supplied by the request; the route path carries no
        // organization, so it is a query parameter exactly like the by-session-id lifecycle commands.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session id can never address a stored session; treat it as hidden (404), never echoing
        // back why.
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenRecap();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent tenant is indistinguishable
            // from a missing session/recap (docs/08: "404 = not found or intentionally hidden"; threat T5).
            return HiddenRecap();
        }

        var context = resolution.Context;

        // Load the session WITHIN the resolved tenant. The lookup leads with the organization id, so a session
        // in another tenant is never returned even when the surrogate id matches; a cross-tenant or unknown
        // session is hidden as 404 (threats T1/T5). The session's own workspace id is then discovered from the
        // loaded row, AFTER the tenant boundary has been enforced — the same shape as the by-session-id
        // lifecycle commands.
        var session = await deps.Sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return HiddenRecap();
        }

        // Object-level authorization: the caller must be a member of the SESSION'S own workspace. The read is
        // allowed to ANY membership role (the same "workspace members" rule as the session list and the scene
        // by-id read), so any membership suffices; a non-member is hidden as 404 (not 403) so resource existence
        // is not leaked (threats T1/T5/T8). The member's actual workspace ROLE then drives the
        // host-vs-participant projection below, so this loads the membership row (FindAsync) scoped by
        // organization id then the session's own workspace id — a control role held in a DIFFERENT workspace
        // never confers standing here.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenRecap();
        }

        // Read the session's recaps tenant- AND workspace-scoped (reusing the existing repository, never a
        // parallel lookup): the list leads with the organization id then the workspace id, so another tenant's
        // or workspace's recaps are never returned even when the session id matches (threats T1/T5). The most
        // recently produced recap (last in produced order) is the one this singular route returns; a session
        // with no recap yet is a hidden 404 (the recap does not exist), never an empty body.
        var recaps = await deps.Recaps
            .ListBySessionAsync(context.OrganizationId, session.WorkspaceId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (recaps.Count == 0)
        {
            return HiddenRecap();
        }

        var recap = recaps[^1];

        // PROJECT BY ROLE through the SAME projector the worker journey exercises (CORE-E2E-003): the
        // host-content and metadata roles (Owner/Admin/Host/CoHost/Auditor) receive the full RecapView WITH the
        // body; the audience roles (Participant/Observer) and any undefined role fall closed to the
        // host-only-field-stripped RecapSummaryView WITHOUT the body, so the host-only recap body never reaches
        // the audience until a separate reveal (docs/09_EVENT_CATALOG.md; threats T2/T8). MembershipRole is
        // non-linear, so the classification is EXACT set membership, never a >/< comparison
        // (RecapProjection.ReceivesFullView).
        var response = RecapProjection.Project(recap, member.Role);
        return Results.Ok(response);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out RecapEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var sessions = services.GetService<ISessionRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var recaps = services.GetService<IRecapRepository>();

        if (resolver is null || sessions is null || workspaceMembers is null || recaps is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new RecapEndpointDependencies(resolver, sessions, workspaceMembers, recaps);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="System.Security.Claims.ClaimsPrincipal"/> to an
    /// <see cref="OidcPrincipal"/>, fail-closed: a failed mapping yields <see langword="false"/> (the caller
    /// returns 401). The mapping error reason is never echoed to the response (threat T7).
    /// </summary>
    private static bool TryMapPrincipal(HttpContext httpContext, out OidcPrincipal principal)
    {
        var result = OidcPrincipalMapper.Map(httpContext.User);
        if (!result.Succeeded)
        {
            principal = null!;
            return false;
        }

        principal = result.Principal;
        return true;
    }

    private static IResult ServiceUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Reading a recap requires persistence, which is not configured.");

    private static IResult Unauthorized()
        => Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult MissingOrganization()
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: $"The '{_organizationSlugQuery}' value is required.");

    // Recap existence is hidden: a malformed session id, a session in a foreign or non-entitled tenant, an
    // unknown session, a session in a workspace the caller does not belong to, and a session with no recap yet
    // are ALL reported as 404, never distinguishable from each other and never echoing the reason (docs/08;
    // threats T1/T5/T8).
    private static IResult HiddenRecap()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct RecapEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        IRecapRepository Recaps);
}
