using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Visibility;

/// <summary>
/// HTTP endpoint of the Visibility module's participant-visible feed
/// (CORE-SES-005: "Implement participant-visible feed endpoint skeleton"). It
/// realizes the documented request flow end-to-end for the single by-participant-id
/// route: "authentication middleware -> tenant/workspace context resolver ->
/// endpoint -> authorization policy" (docs/02_ARCHITECTURE.md), mirroring
/// <see cref="Sessions.SessionEndpoints"/> exactly.
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
/// <c>GetVisibleResourcesForParticipant</c>. This story creates the Visibility
/// module's FIRST file: the skeleton endpoint. The visibility engine itself
/// (<c>CanViewResource</c> / <c>GetVisibleResourcesForParticipant</c> /
/// <c>PreviewVisibilityForHost</c>, the visibility rules and audience math) arrives
/// in the CORE-VIS epic and is deliberately NOT built here.
///
/// SKELETON SCOPE — THE FEED IS EMPTY. The route, its fail-closed authorization and
/// a participant-SAFE feed envelope are the deliverable; the ACTUAL visible content
/// (filtered reveal events, content blocks, server-side visibility-rule evaluation)
/// is produced by the later Visibility + Reveal + Realtime epics (CORE-VIS-* /
/// CORE-RT-*). There is no visibility engine, no content and no session events yet
/// (csv/database_tables.csv assigns <c>session_events</c> to the Realtime module),
/// so the feed is LEGITIMATELY empty — an empty
/// <see cref="ParticipantVisibleFeedResponse"/>, never a stub that fabricates
/// content.
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

    // GET /api/v1/participants/{participantId}/visible-feed?organizationSlug={slug}
    private static async Task<IResult> GetParticipantVisibleFeedAsync(
        HttpContext httpContext,
        string participantId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
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

        // Authorized. The feed is legitimately EMPTY in this skeleton (there is no
        // visibility engine and no content yet — the filtered content is the
        // CORE-VIS/Reveal/Realtime epics). Return the participant-safe envelope
        // with an empty item list and a server timestamp from the injected
        // TimeProvider (docs/08: "Include server timestamps").
        var feed = ParticipantVisibleFeedResponse.Empty(
            participant.Id,
            participant.WorkspaceId,
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
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null
            || participants is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new VisibilityEndpointDependencies(resolver, participants, workspaceMembers);
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
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "The participant feed requires persistence, which is not configured.");

    private static IResult Unauthorized()
        => Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult MissingOrganization()
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
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
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct VisibilityEndpointDependencies(
        TenantContextResolver Resolver,
        IParticipantRepository Participants,
        IWorkspaceMemberRepository WorkspaceMembers);
}
