using System.Text.Json;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Visibility;

/// <summary>
/// HTTP endpoint of the Visibility module's reveal command (CORE-VIS-004: "Implement reveal command
/// with idempotency"). It realizes the documented request flow for the reveal route
/// (authentication -> tenant/workspace context resolver -> endpoint -> authorization policy ->
/// command, docs/02_ARCHITECTURE.md), mirroring <see cref="Sessions.SessionEndpoints"/>'s start/end
/// commands — this is the Visibility module's FIRST command endpoint.
///
/// Route owned by this story (csv/api_routes.csv line 14):
/// <list type="bullet">
///   <item><c>POST /api/v1/sessions/{sessionId}/reveal</c> — module <b>Visibility</b>, roles
///   "Host,CoHost,Owner,Admin", "Idempotent reveal". The route is under <c>/sessions</c> (the session
///   pins the workspace) but is owned by the Visibility module.</item>
/// </list>
///
/// Authoritative behavior — the resource is made VISIBLE to the audience, idempotently. The reveal
/// sets the target resource's visibility rule to <see cref="VisibilityState.Visible"/>
/// (<see cref="RevealService"/>, reusing the CORE-VIS-001 aggregate) and, when that actually changes the
/// visibility, appends an append-only AUDIT record (CORE-VIS-006) capturing the authenticated actor (the
/// resolved tenant context's user profile id), the resource, the optional selected-participant target and
/// the before/after state. The durable realtime EVENT, by contrast, IS DEFERRED to the Realtime epic
/// exactly as CORE-SES-004 deferred SessionStarted/SessionEnded: the
/// <c>ContentRevealed</c>/<c>VisibilityRuleChanged</c> session events (docs/09_EVENT_CATALOG.md) and
/// their delivery are CORE-RT-003, and the <c>session_events</c> table is Realtime-owned. The persisted
/// visibility change and its audit record are the behavior delivered here.
///
/// IDEMPOTENCY (docs/08_API_CONTRACTS.md). A client-supplied <c>Idempotency-Key</c> request HEADER is
/// REQUIRED; a repeated reveal with the same key does not apply a second effect
/// (<see cref="RevealService"/> records keys in the System module's <c>idempotency_keys</c> table and
/// short-circuits a retry). Both a first apply and an idempotent retry return 200 — the command is
/// idempotent and both leave the resource visible.
///
/// Tenant resolution + authorization mirror the session start/end commands exactly: the route path
/// carries only <c>{sessionId}</c>, the target organization is the body's <c>organizationSlug</c>
/// resolved by <see cref="TenantContextResolver"/> (token claim AND persisted membership, threat T5),
/// the session is loaded WITHIN the resolved tenant (org boundary leads), and the caller is authorized
/// by their role in the SESSION'S OWN workspace. Fail-closed at every step and never leaking why:
/// <list type="bullet">
///   <item>503 when persistence is off; 401 when the principal cannot be mapped.</item>
///   <item>A malformed body or a missing <c>organizationSlug</c> is 400; a malformed session id, a
///   denied tenant resolution, a session not in the tenant, and a caller who is not a member of the
///   session's workspace are ALL hidden as 404 (never distinguishable, never 403 for a non-member;
///   threats T1/T5).</item>
///   <item>A known workspace member who lacks the reveal role (Owner/Admin/Host/CoHost — the "Execute
///   reveal" row of docs/06_AUTHORIZATION_MATRIX.md) is 403. <see cref="MembershipRole"/> is
///   non-linear, so the role check is EXACT, never an ordering comparison.</item>
///   <item>Only AFTER authorization is the rest of the request validated (so an unauthorized caller
///   never receives request-shape feedback): a missing <c>Idempotency-Key</c> header, an unknown or
///   numeric <c>resourceType</c>, and an empty <c>resourceId</c> are each 400.</item>
/// </list>
///
/// Same-workspace coupling of the target resource (that <c>resourceId</c> refers to a real resource
/// in the session's workspace) is the documented carry-over deferred to a resource-resolution step,
/// mirroring <c>ContentBlock.SceneId</c> / <c>Entity.EntityTypeId</c>: revealing a (type, id) sets its
/// rule visible without resolving the resource. This is not a security gap — the rule is workspace-
/// scoped and only a Host-capable member may create it; a rule for a non-existent resource resolves to
/// nothing.
///
/// Persistence dependency: like the session endpoints, this uses the repositories, the tenant context
/// resolver and the System idempotency store, which are registered only when a database connection
/// string is configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed
/// with 503.
/// </summary>
internal static class RevealEndpoints
{
    /// <summary>The request header carrying the client idempotency key (docs/08_API_CONTRACTS.md).</summary>
    private const string _idempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapRevealEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid
        // token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        group.MapPost("/{sessionId}/reveal", RevealAsync);

        return endpoints;
    }

    // POST /api/v1/sessions/{sessionId}/reveal
    private static async Task<IResult> RevealAsync(
        HttpContext httpContext,
        string sessionId,
        [FromBody] RevealRequest? request,
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

        // A missing/unparseable body cannot carry the target organization or resource; 400. (Malformed
        // JSON is rejected as 400 by the framework before the handler.)
        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        // The target organization is required to resolve the tenant; it is supplied in the body
        // (this route has a body, like the workspace/scene create commands).
        if (string.IsNullOrWhiteSpace(request.OrganizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session id can never address a stored session; hidden as 404.
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenSession();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenSession();
        }

        var context = resolution.Context;

        // Load the session WITHIN the resolved tenant (org boundary leads); a cross-tenant or unknown
        // session is hidden as 404. The session's workspace is then discovered from the loaded row.
        var session = await deps.Sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return HiddenSession();
        }

        // Object-level authorization: the caller must be a member of the SESSION'S workspace; a
        // non-member is hidden as 404 (not 403), the same rule as the session start/end commands.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenSession();
        }

        // The caller is a known member of the session's workspace, so an insufficient role is 403. The
        // reveal roles are Owner/Admin/Host/CoHost (the "Execute reveal" row of
        // docs/06_AUTHORIZATION_MATRIX.md; csv/api_routes.csv "Host,CoHost,Owner,Admin"). MembershipRole
        // is non-linear, so this is an EXACT set membership check.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized. Only now validate the rest of the request, so an unauthorized caller never
        // receives request-shape feedback.

        // The Idempotency-Key header is required for reveal commands (docs/08_API_CONTRACTS.md).
        if (!TryGetIdempotencyKey(httpContext, out var idempotencyKey))
        {
            return ValidationError($"The '{_idempotencyKeyHeader}' header is required.");
        }

        // The resource kind is parsed by NAME; a numeric or unknown value is rejected (mirrors the
        // content-block type parsing).
        if (!TryParseResourceType(request.ResourceType, out var resourceType))
        {
            return ValidationError("The 'resourceType' value is not a recognized resource type.");
        }

        if (request.ResourceId == Guid.Empty)
        {
            return ValidationError("The 'resourceId' value is required.");
        }

        // Optional SELECTED-participant target (CORE-VIS-005). Absent/null -> reveal to the whole
        // audience. A present-but-empty id is a 400. A set id must be a participant of the SESSION'S
        // OWN workspace: it is resolved within the resolved tenant and its workspace is verified, so a
        // cross-tenant or cross-workspace (or unknown) participant is hidden as 404 — a host must not
        // be able to target, or probe for, a participant outside the session's workspace (threat T5).
        Guid? targetParticipantId = null;
        if (request.ParticipantId is { } requestedParticipantId)
        {
            if (requestedParticipantId == Guid.Empty)
            {
                return ValidationError("The 'participantId' value must not be empty.");
            }

            var participant = await deps.Participants
                .FindByIdInOrganizationAsync(context.OrganizationId, requestedParticipantId, cancellationToken)
                .ConfigureAwait(false);
            if (participant is null || participant.WorkspaceId != session.WorkspaceId)
            {
                return HiddenSession();
            }

            targetParticipantId = requestedParticipantId;
        }

        var now = timeProvider.GetUtcNow();

        var result = await deps.Reveal
            .RevealAsync(
                context.OrganizationId,
                session.WorkspaceId,
                resourceType,
                request.ResourceId,
                targetParticipantId,
                context.UserProfileId,
                idempotencyKey,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        // REALTIME EVENT (CORE-RT-003): emit the durable ContentRevealed event IFF the reveal actually
        // changed visibility — the same change signal the audit uses (CORE-VIS-006), so a retry or a
        // no-op reveal of an already-visible resource emits nothing. The Realtime publisher appends the
        // event to the session's append-only stream and delivers it to the server-computed recipient
        // groups (the selected participant, or the audience); a non-selected participant is not in the
        // target group and so cannot receive it (threat T3). The payload carries resource IDENTIFIERS
        // only, never resolved content (threat T7/T2).
        if (result.VisibilityChanged)
        {
            var payload = JsonSerializer.Serialize(new RevealEventPayload(
                result.ResourceType.ToString(),
                result.ResourceId));

            var sessionEvent = SessionEvent.Create(
                context.OrganizationId,
                session.WorkspaceId,
                sessionGuid,
                SessionEventTypes.ContentRevealed,
                context.UserProfileId,
                targetParticipantId,
                payload,
                schemaVersion: 1,
                now);

            await deps.EventPublisher.PublishAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        }

        return Results.Ok(RevealResponse.From(result));
    }

    /// <summary>
    /// The server-composed payload of a <c>ContentRevealed</c> event: the generic kind and id of the
    /// revealed resource. Identifiers only — never the resolved content (threat T7).
    /// </summary>
    private sealed record RevealEventPayload(string ResourceType, Guid ResourceId);

    /// <summary>
    /// Reads the required <c>Idempotency-Key</c> header. Returns <see langword="false"/> when it is
    /// absent or blank.
    /// </summary>
    private static bool TryGetIdempotencyKey(HttpContext httpContext, out string idempotencyKey)
    {
        if (httpContext.Request.Headers.TryGetValue(_idempotencyKeyHeader, out var values))
        {
            var value = values.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                idempotencyKey = value.Trim();
                return true;
            }
        }

        idempotencyKey = string.Empty;
        return false;
    }

    /// <summary>
    /// Parses a <see cref="VisibilityResourceType"/> from its NAME only, rejecting null/blank, numeric
    /// values and unknown names — so a client cannot smuggle in an undefined enum value by number
    /// (mirrors the content-block type and role parsing).
    /// </summary>
    private static bool TryParseResourceType(string? value, out VisibilityResourceType resourceType)
    {
        resourceType = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // A numeric string must not bind to an enum member by value.
        if (int.TryParse(value, out _))
        {
            return false;
        }

        return Enum.TryParse(value, ignoreCase: false, out resourceType)
            && VisibilityRule.IsValidResourceType(resourceType);
    }

    private static bool TryGetDependencies(HttpContext httpContext, out RevealEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var sessions = services.GetService<ISessionRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var participants = services.GetService<IParticipantRepository>();
        var reveal = services.GetService<RevealService>();
        var eventPublisher = services.GetService<ISessionEventPublisher>();

        if (resolver is null
            || sessions is null
            || workspaceMembers is null
            || participants is null
            || reveal is null
            || eventPublisher is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new RevealEndpointDependencies(
            resolver, sessions, workspaceMembers, participants, reveal, eventPublisher);
        return true;
    }

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
            detail: "The reveal command requires persistence, which is not configured.");

    private static IResult Unauthorized()
        => Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult Forbidden()
        => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: "You are not authorized to perform this action.");

    private static IResult MissingOrganization()
        => ValidationError("The 'organizationSlug' value is required.");

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: detail);

    // Session existence is hidden: a malformed id, a session in a foreign or non-entitled tenant, an
    // unknown session, and a session in a workspace the caller does not belong to are ALL reported as
    // 404, never distinguishable and never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenSession() => NotFound();

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct RevealEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        IParticipantRepository Participants,
        RevealService Reveal,
        ISessionEventPublisher EventPublisher);
}
