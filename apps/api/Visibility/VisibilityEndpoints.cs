using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Visibility;

/// <summary>
/// HTTP endpoint of the Visibility module's participant-visible feed (route skeleton
/// CORE-SES-005, real projection CORE-API-005: "Implement the real participant
/// visible-feed projection"). It realizes the documented request flow end-to-end for
/// the single by-participant-id route: "authentication middleware -> tenant/workspace
/// context resolver -> endpoint -> authorization policy" (docs/02_ARCHITECTURE.md),
/// mirroring <see cref="Sessions.SessionEndpoints"/> exactly.
///
/// Route owned by this story (csv/api_routes.csv line 18):
/// <list type="bullet">
///   <item><c>GET /api/v1/participants/{participantId}/visible-feed</c> — module
///   <b>Visibility</b>, roles "Participant owner or Host", "Participant-safe
///   feed".</item>
/// </list>
///
/// WHY THE VISIBILITY MODULE OWNS THIS ROUTE. csv/api_routes.csv assigns this route
/// to the Visibility module, and docs/05_MODULE_CONTRACTS.md gives the Visibility
/// module "audience calculations", "preview-as-participant" and "visible state
/// reconstruction" and the provided operation
/// <c>GetVisibleResourcesForParticipant</c>. The route skeleton (CORE-SES-005)
/// established the Visibility module's first endpoint with its fail-closed
/// authorization; CORE-API-005 wires the handler to the now participant-aware
/// <see cref="VisibilityPreviewService"/> so the feed returns the participant's real
/// visible set.
///
/// THE REAL VISIBLE SET (CORE-API-005). After the fail-closed authorization below, the
/// feed is computed server-side by
/// <see cref="VisibilityPreviewService.GetVisibleResourcesForParticipantAsync"/> (the
/// participant-aware preview, CORE-API-004), which decides every candidate resource
/// through the central <see cref="VisibilityPolicy"/> — the SAME primitive the realtime
/// recipient resolver (<see cref="EventRecipientVisibility"/>) uses — so a participant
/// sees a resource iff an audience-wide visible rule, or a visible rule scoped to
/// exactly them, applies, and the REST feed can never diverge from realtime delivery or
/// per-resource access (docs/05_MODULE_CONTRACTS.md "Do not duplicate visibility logic
/// elsewhere"). A resource revealed only to a DIFFERENT participant is excluded (the
/// selected-participant guarantee; threat T5). Each visible resource is projected to a
/// participant-SAFE feed item carrying only the resource IDENTITY (kind + id), matching
/// the realtime audience event payload
/// (<see cref="Realtime.SessionEventEnvelope.ForAudience"/>) — never resolved content
/// (threats T2/T7). The feed is empty only when the participant currently has nothing
/// visible, never by construction.
///
/// Tenant resolution (mirrors the session/workspace by-id routes): the route path
/// carries only <c>{participantId}</c>, so the target organization is supplied by a
/// required <c>organizationSlug</c> QUERY parameter and turned into a trusted
/// <see cref="TenantContext"/> by <see cref="TenantContextResolver"/> (token
/// organization claim AND persisted organization membership — defence in depth,
/// threat T5). The participant is then loaded WITHIN that resolved organization, and
/// the caller is authorized against the participant's own workspace.
///
/// LIMITATION — ORG-AFFILIATED CALLERS ONLY (intentional for the skeleton, not a
/// bug). Because resolution requires the caller to be a persisted organization
/// member of the resolved tenant, this HTTP skeleton serves only org-affiliated
/// callers. The broad EXTERNAL/anonymous-participant feed delivery — a participant
/// reached over the realtime hub with participant-identity resolution and scoped
/// tokens, not an organization membership (docs/11_REALTIME_SYNC.md:
/// <c>session:{sessionId}:participant:{participantId}</c> groups and per-participant
/// payload projection) — is a Realtime-epic follow-up and is NOT in scope here.
///
/// Authorization model (object-level, server-side; the matrix row "View own visible
/// feed" in csv/authorization_matrix.csv = owner:no, admin:no, host:preview,
/// cohost:preview, participant:yes, observer:no, auditor:no, which
/// docs/06_AUTHORIZATION_MATRIX.md states as "View own participant feed"; threats
/// T1/T5), load-then-authorize, fail-closed at every step and NEVER leaking why.
/// The feed is PRIVATE, so EVERY denial — including every authorization refusal — is
/// hidden as 404, never 403: a caller who may not read the feed must not even learn
/// the participant exists (the same object-level 404-hide rule as the workspace and
/// session read-one routes; docs/08: "404 = not found or intentionally hidden").
/// Access is ALLOWED (200) iff EITHER:
/// <list type="bullet">
///   <item>OWN FEED — the resolved caller's user OWNS the participant
///   (<c>context.UserProfileId == participant.UserProfileId</c>). This is
///   ownership-based and independent of the caller's organization/workspace ROLE
///   (the matrix grants the participant audience role "yes" on its OWN feed and
///   grants Owner/Admin "no"). An anonymous participant has no user link, so
///   own-feed is impossible for it.</item>
///   <item>PREVIEW — the caller is a <see cref="MembershipRole.Host"/> OR
///   <see cref="MembershipRole.CoHost"/> of the PARTICIPANT'S OWN workspace
///   (<see cref="IWorkspaceMemberRepository.HasRoleAsync"/> on
///   <see cref="Participant.WorkspaceId"/>). The matrix grants BOTH host and cohost
///   "preview"; csv/api_routes.csv summarizes this as "Host", but the granular
///   matrix is authoritative, so CoHost is included.</item>
/// </list>
/// EVERYTHING ELSE is DENIED as 404: an Owner/Admin who is neither the
/// participant-owner nor a Host/CoHost of the participant's workspace; an
/// Observer/Auditor; a DIFFERENT participant; a Host of a DIFFERENT workspace; a
/// cross-tenant or unknown participant. <see cref="MembershipRole"/> is non-linear,
/// so the role check is EXACT (Host or CoHost), never an ordering comparison.
///
/// REMOVED PARTICIPANT -> 404. A participant whose status is
/// <see cref="ParticipantStatus.Removed"/> holds no standing (ParticipantStatus
/// xmldoc: "a removed participant holds no standing and is treated as gone"), so its
/// feed is not served and is hidden as 404 like any other denial.
///
/// Persistence dependency: like the session/workspace endpoints, this uses the
/// repositories and the tenant context resolver, which are registered only when a
/// database connection string is configured (see <c>Program.cs</c>). When
/// persistence is off the endpoint fails closed with 503 rather than crashing
/// startup.
/// </summary>
internal static class VisibilityEndpoints
{
    /// <summary>Required query parameter naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    /// <summary>
    /// Required query parameter naming the session the feed is scoped to (CORE-SVIS-001). A reveal is
    /// session-scoped, so the feed is "what this participant may see IN this session"; a reveal in a
    /// concurrent session of the same workspace is never in it (the cross-session leak; threat T5/T3).
    /// </summary>
    private const string _sessionIdQuery = "sessionId";

    public static IEndpointRouteBuilder MapVisibilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so
        // a missing/invalid token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/participants")
            .RequireAuthorization();

        group.MapGet("/{participantId}/visible-feed", GetParticipantVisibleFeedAsync);

        return endpoints;
    }

    // GET /api/v1/participants/{participantId}/visible-feed?organizationSlug={slug}&sessionId={sessionId}
    private static async Task<IResult> GetParticipantVisibleFeedAsync(
        HttpContext httpContext,
        string participantId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        [FromQuery(Name = _sessionIdQuery)] string? sessionId,
        TimeProvider timeProvider,
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

        // The target organization is required and supplied by the request; the
        // route path carries no organization, so it is a query parameter exactly
        // like the session by-id routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed participant id can never address a stored participant; treat
        // it as hidden (404), never echoing back why.
        if (!Guid.TryParse(participantId, out var participantGuid) || participantGuid == Guid.Empty)
        {
            return HiddenFeed();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the participant as 404 so a foreign
            // or non-existent tenant is indistinguishable from a missing
            // participant (docs/08; threat T5).
            return HiddenFeed();
        }

        var context = resolution.Context;

        // Load the participant WITHIN the resolved tenant. The lookup leads with
        // the organization id, so a participant in another tenant is never returned
        // even when the surrogate id matches; a cross-tenant or unknown participant
        // is hidden as 404 (threats T1/T5). The participant's own workspace id,
        // user link and status are then discovered from the loaded row, AFTER the
        // tenant boundary has been enforced.
        var participant = await deps.Participants
            .FindByIdInOrganizationAsync(context.OrganizationId, participantGuid, cancellationToken)
            .ConfigureAwait(false);
        if (participant is null)
        {
            return HiddenFeed();
        }

        // A removed participant holds no standing (ParticipantStatus.Removed), so
        // its feed is not served. Hidden as 404 like every other denial: the feed
        // is private, so a caller must not learn a removed participant ever
        // existed.
        if (participant.Status != ParticipantStatus.Active)
        {
            return HiddenFeed();
        }

        // Object-level authorization. The feed is private, so EVERY refusal is a
        // hidden 404 (never 403): a caller who may not read it must not learn the
        // participant exists (threats T1/T5; docs/08 "404 = ... intentionally
        // hidden").
        var authorized = await IsAuthorizedAsync(deps, context, participant, cancellationToken)
            .ConfigureAwait(false);
        if (!authorized)
        {
            return HiddenFeed();
        }

        // Authorized. Only now validate the request-shape session scope, so an unauthorized caller never
        // receives request-shape feedback (mirrors the reveal command). The feed is SESSION-SCOPED
        // (CORE-SVIS-001): a reveal is visible only within its session, so the feed must name which session
        // it is for. A missing/blank sessionId is a 400; a malformed one is a 400.
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return MissingSession();
        }

        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return ValidationError($"The '{_sessionIdQuery}' value is not a valid session id.");
        }

        // The session must exist WITHIN the resolved tenant and be the participant's OWN workspace's
        // session. The lookup leads with the organization id, so a session in another tenant is never
        // returned; a cross-tenant, cross-workspace or unknown session is hidden as 404 — a caller must
        // not be able to probe for a session outside the participant's workspace (threats T1/T5). The feed
        // would be empty for such a session anyway (its rules never match this workspace), but hiding it
        // keeps the private-feed posture consistent.
        var session = await deps.Sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (session is null || session.WorkspaceId != participant.WorkspaceId)
        {
            return HiddenFeed();
        }

        // Compute the participant's ACTUALLY-VISIBLE set server-side for THIS SESSION (CORE-API-005 +
        // CORE-SVIS-001) through the participant-aware preview query (CORE-API-004), which routes every
        // candidate resource through the central session-scoped VisibilityPolicy — so this REST feed, the
        // realtime recipient calculation and per-resource access can never diverge and the visibility
        // decision lives in exactly one place (docs/05_MODULE_CONTRACTS.md "Do not duplicate visibility
        // logic elsewhere"). The set is the resources visible to THIS participant IN THIS SESSION: an
        // audience-wide reveal OR a reveal scoped to exactly them, never one revealed only to a different
        // participant (the selected-participant guarantee) and never one revealed in a concurrent session
        // (the session-scope guarantee), decided fail-closed by the policy; threat T5/T3. It is scoped to
        // the participant's own tenant and workspace, both already enforced above.
        var visibleResources = await deps.Preview
            .GetVisibleResourcesForParticipantAsync(
                context.OrganizationId,
                participant.WorkspaceId,
                session.Id,
                participant.Id,
                cancellationToken)
            .ConfigureAwait(false);

        // Project each visible resource to its participant-safe item (kind + id only,
        // never resolved content — threat T7) and return the participant-safe envelope
        // with a server timestamp from the injected TimeProvider (docs/08: "Include
        // server timestamps").
        var feed = ParticipantVisibleFeedResponse.From(
            participant.Id,
            participant.WorkspaceId,
            visibleResources,
            timeProvider.GetUtcNow());

        return Results.Ok(feed);
    }

    /// <summary>
    /// The object-level authorization decision for the participant-visible feed:
    /// ALLOW iff the caller OWNS the participant (own feed) OR is a Host/CoHost of
    /// the participant's own workspace (preview). The role check is EXACT
    /// (<see cref="MembershipRole"/> is non-linear), and Host/CoHost membership is
    /// checked against the PARTICIPANT'S workspace, so a control role held in a
    /// different workspace never confers standing here (threats T1/T5;
    /// csv/authorization_matrix.csv "View own visible feed":
    /// host:preview / cohost:preview / participant:yes, all others no).
    /// </summary>
    private static async Task<bool> IsAuthorizedAsync(
        VisibilityEndpointDependencies deps,
        TenantContext context,
        Participant participant,
        CancellationToken cancellationToken)
    {
        // OWN FEED: the caller's user owns the participant. The participant must be
        // user-linked; BelongsToSubject is false for an anonymous participant (no
        // user link) and for an empty subject id, so own-feed is impossible for an
        // anonymous participant. This is ownership-based and independent of the
        // caller's organization/workspace role.
        if (participant.BelongsToSubject(context.UserProfileId))
        {
            return true;
        }

        // PREVIEW: the caller is a Host OR CoHost of the PARTICIPANT'S own
        // workspace. The matrix grants both "preview"; csv/api_routes.csv's "Host"
        // shorthand is widened to the granular matrix here. The lookup is scoped by
        // organization id then the participant's workspace id, so a Host/CoHost
        // role held in another workspace is never consulted (threat T5).
        var isHost = await deps.WorkspaceMembers
            .HasRoleAsync(
                context.OrganizationId,
                participant.WorkspaceId,
                context.UserProfileId,
                MembershipRole.Host,
                cancellationToken)
            .ConfigureAwait(false);
        if (isHost)
        {
            return true;
        }

        var isCoHost = await deps.WorkspaceMembers
            .HasRoleAsync(
                context.OrganizationId,
                participant.WorkspaceId,
                context.UserProfileId,
                MembershipRole.CoHost,
                cancellationToken)
            .ConfigureAwait(false);

        return isCoHost;
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They
    /// exist only when a database connection string is configured; when absent, the
    /// endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out VisibilityEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var participants = services.GetService<IParticipantRepository>();
        var sessions = services.GetService<ISessionRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var preview = services.GetService<VisibilityPreviewService>();

        if (resolver is null
            || participants is null
            || sessions is null
            || workspaceMembers is null
            || preview is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new VisibilityEndpointDependencies(resolver, participants, sessions, workspaceMembers, preview);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="ClaimsPrincipal"/> to an
    /// <see cref="OidcPrincipal"/>, fail-closed: a failed mapping yields
    /// <see langword="false"/> (the caller returns 401). The mapping error reason is
    /// never echoed to the response (threat T7).
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
        => CoreProblem.Create(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            code: ProblemCodes.ServiceUnavailable,
            title: "Service Unavailable",
            detail: "The participant feed requires persistence, which is not configured.");

    private static IResult Unauthorized()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status401Unauthorized,
            code: ProblemCodes.AuthenticationRequired,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult MissingOrganization()
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult MissingSession()
        => ValidationError($"The '{_sessionIdQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

    // Participant-feed access is hidden: a malformed id, a participant in a foreign
    // or non-entitled tenant, an unknown participant, a removed participant, and a
    // caller who is neither the participant-owner nor a Host/CoHost of the
    // participant's workspace are ALL reported as 404, never distinguishable from
    // each other and never echoing the reason. The feed is private, so even an
    // authorization refusal is hidden as 404 rather than 403 (docs/08; threats
    // T1/T5/T7).
    private static IResult HiddenFeed() => NotFound();

    private static IResult NotFound()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct VisibilityEndpointDependencies(
        TenantContextResolver Resolver,
        IParticipantRepository Participants,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        VisibilityPreviewService Preview);
}
