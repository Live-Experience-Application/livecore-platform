using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Realtime;

/// <summary>
/// HTTP endpoint of the Realtime module's reconnect replay (CORE-RT-005: "Implement reconnect replay with
/// filtering"). It realizes the documented request flow for the replay route
/// (authentication -&gt; tenant/session context resolver -&gt; endpoint -&gt; per-recipient visibility
/// filter, docs/02_ARCHITECTURE.md; docs/09_EVENT_CATALOG.md "Reconnect replay"), mirroring
/// <see cref="Sessions.SessionEndpoints"/> / <see cref="Visibility.RevealEndpoints"/> for the by-session-id
/// routes.
///
/// Route owned by this story (csv/api_routes.csv line 13):
/// <list type="bullet">
///   <item><c>GET /api/v1/sessions/{sessionId}/events</c> — module <b>Realtime</b>, roles "session
///   audience", "Filtered replay". The route is under <c>/sessions</c> (the session pins the workspace)
///   but is owned by the Realtime module, which owns "reconnect replay" (docs/05_MODULE_CONTRACTS.md).</item>
/// </list>
///
/// AUTHORIZATION = THE LIVE CONNECTION, REUSED. The caller's authorized relationship to the session — and
/// the SERVER-MANAGED groups it maps to — is resolved by the very same <see cref="RealtimeConnectionResolver"/>
/// the live hub uses on connect (CORE-RT-002): the caller supplies only identifiers
/// (<c>?organizationSlug=</c>, the path <c>{sessionId}</c>, and for a participant <c>?participantId=</c>),
/// never group names, and the server computes the groups. So a host replays as a host, an observer as an
/// observer, and a participant only its OWN feed — a caller can never replay another participant's feed
/// (they do not own that participant record, so the resolver denies). The replay then keeps only the
/// events delivered to the caller's groups, with the same projection live delivery uses
/// (<see cref="SessionReplayService"/>), so "reconnect replay filters events again" and never leaks a
/// hidden event (threat T3 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// FAIL-CLOSED, NEVER LEAKING WHY (the stream is private, like the participant-visible feed):
/// <list type="bullet">
///   <item>503 when persistence is off; 401 when the principal cannot be mapped.</item>
///   <item>A missing <c>organizationSlug</c> is 400; a malformed session id, a malformed
///   <c>participantId</c>, a denied tenant, an unknown session, and a caller with no legitimate
///   relationship to the session (including a <c>participantId</c> the caller does not own) are ALL hidden
///   as 404 — never distinguishable, never 403 — so a probe learns nothing about what exists
///   (threats T1/T5; docs/08 "404 = not found or intentionally hidden").</item>
///   <item>Only AFTER authorization is the optional replay cursor validated, so an unauthorized caller
///   never receives request-shape feedback: a present-but-malformed <c>afterSequence</c> is 400.</item>
/// </list>
///
/// Persistence dependency: like the session/reveal endpoints, this uses the tenant context resolver, the
/// realtime connection resolver and the replay service, registered only when a database connection string
/// is configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class SessionEventReplayEndpoints
{
    /// <summary>Required query parameter naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    /// <summary>Optional query parameter identifying the participant viewpoint (a participant replaying its own feed).</summary>
    private const string _participantIdQuery = "participantId";

    /// <summary>Optional query parameter carrying the caller's last acknowledged sequence number (the replay cursor).</summary>
    private const string _afterSequenceQuery = "afterSequence";

    public static IEndpointRouteBuilder MapSessionEventReplayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token
        // is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        group.MapGet("/{sessionId}/events", ReplaySessionEventsAsync);

        return endpoints;
    }

    // GET /api/v1/sessions/{sessionId}/events?organizationSlug={slug}&participantId={pid}&afterSequence={seq}
    private static async Task<IResult> ReplaySessionEventsAsync(
        HttpContext httpContext,
        string sessionId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        [FromQuery(Name = _participantIdQuery)] string? participantId,
        [FromQuery(Name = _afterSequenceQuery)] string? afterSequence,
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

        // The target organization is required and supplied by the request; the route path carries no
        // organization, so it is a query parameter exactly like the session start/end commands.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session id can never address a stored session; hidden as 404.
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenStream();
        }

        // Optional participant viewpoint: a participant replaying its OWN feed identifies as that
        // participant, exactly like the hub connection's participantId. A present-but-malformed value can
        // never address a real participant, so it is hidden as 404 (never echoing why).
        Guid? participantGuid = null;
        if (!string.IsNullOrWhiteSpace(participantId))
        {
            if (!Guid.TryParse(participantId, out var parsedParticipant) || parsedParticipant == Guid.Empty)
            {
                return HiddenStream();
            }

            participantGuid = parsedParticipant;
        }

        // Authorize and resolve the caller's server-managed groups EXACTLY as the live hub connection does
        // (CORE-RT-002), so the replay viewpoint can never diverge from the live one. Any denial — a
        // foreign/non-entitled tenant, an unknown session, no legitimate relationship, or a participant the
        // caller does not own — is hidden as 404 (the stream is private; never 403).
        var admission = await deps.ConnectionResolver
            .ResolveAsync(httpContext.User, organizationSlug, sessionGuid, participantGuid, cancellationToken)
            .ConfigureAwait(false);
        if (!admission.Admitted)
        {
            return HiddenStream();
        }

        // Resolve the tenant id for the (tenant- and session-scoped) event read via the same
        // token-claim-AND-membership check the connection resolver itself used; it admitted, so this
        // resolves. A defensive denial here is hidden as 404 too.
        var resolution = await deps.TenantResolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenStream();
        }

        // Authorized. Only now validate the optional replay cursor, so an unauthorized caller never
        // receives request-shape feedback (mirrors the reveal command). The cursor is the caller's last
        // acknowledged per-session SEQUENCE number (CORE-RTC-001): a present-but-malformed or negative value
        // is a 400; an absent cursor replays from the start.
        long? afterSequenceValue = null;
        if (!string.IsNullOrWhiteSpace(afterSequence))
        {
            if (!long.TryParse(afterSequence, out var parsedCursor) || parsedCursor < 0)
            {
                return ValidationError($"The '{_afterSequenceQuery}' value is not a valid sequence number.");
            }

            afterSequenceValue = parsedCursor;
        }

        var envelopes = await deps.Replay
            .ReplayAsync(
                resolution.Context.OrganizationId,
                sessionGuid,
                admission.Groups,
                afterSequenceValue,
                cancellationToken)
            .ConfigureAwait(false);

        var response = SessionEventReplayResponse.From(sessionGuid, envelopes, timeProvider.GetUtcNow());

        return Results.Ok(response);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a
    /// database connection string is configured; when absent, the endpoint fails closed with 503 instead
    /// of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out ReplayEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var tenantResolver = services.GetService<TenantContextResolver>();
        var connectionResolver = services.GetService<RealtimeConnectionResolver>();
        var replay = services.GetService<SessionReplayService>();

        if (tenantResolver is null
            || connectionResolver is null
            || replay is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new ReplayEndpointDependencies(tenantResolver, connectionResolver, replay);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="System.Security.Claims.ClaimsPrincipal"/> to an
    /// <see cref="OidcPrincipal"/>, fail-closed: a failed mapping yields <see langword="false"/> (the
    /// caller returns 401). The mapping error reason is never echoed to the response (threat T7).
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
            detail: "The session event replay requires persistence, which is not configured.");

    private static IResult Unauthorized()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status401Unauthorized,
            code: ProblemCodes.AuthenticationRequired,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult MissingOrganization()
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

    // Stream access is hidden: a malformed session/participant id, a session in a foreign or non-entitled
    // tenant, an unknown session, and a caller with no legitimate relationship to the session are ALL
    // reported as 404, never distinguishable and never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenStream() => NotFound();

    private static IResult NotFound()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct ReplayEndpointDependencies(
        TenantContextResolver TenantResolver,
        RealtimeConnectionResolver ConnectionResolver,
        SessionReplayService Replay);
}
