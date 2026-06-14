using System.Text.Json;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Visibility;

/// <summary>
/// HTTP endpoint of the Visibility module's hide command (CORE-REV-001, the "Reveal Lifecycle" hide /
/// un-reveal). It is the exact inverse of <see cref="RevealEndpoints"/> and is intentionally a faithful
/// mirror of it: same authenticated route group under <c>/api/v1/sessions</c>, same tenant resolution,
/// same object-level authorization and the same fail-closed status mapping — only the direction of the
/// visibility change differs (Visible -> Hidden) and the durable event is <c>ContentHidden</c> rather than
/// <c>ContentRevealed</c>. It reuses the SAME <see cref="RevealService"/> (and so the same
/// <c>IdempotencyKeyStore</c> and audit producer), so hide is not a parallel duplicate of reveal.
///
/// Route owned by this story:
/// <list type="bullet">
///   <item><c>POST /api/v1/sessions/{sessionId}/hide</c> — module <b>Visibility</b>, roles
///   "Host,CoHost,Owner,Admin". The route is under <c>/sessions</c> (the session pins the workspace) but
///   is owned by the Visibility module, exactly like the reveal route.</item>
/// </list>
///
/// Authoritative behavior — the resource is made HIDDEN from the audience again, idempotently. The hide
/// flips the target resource's visibility rule to <see cref="VisibilityState.Hidden"/>
/// (<see cref="RevealService.HideAsync"/>, reusing the CORE-VIS-001 aggregate's
/// <see cref="VisibilityRule.ChangeVisibility"/> primitive) and, when that actually changes the
/// visibility, appends an append-only AUDIT record (CORE-VIS-006) capturing the authenticated actor, the
/// resource, the optional selected-participant target and the before/after state (Visible -> Hidden).
/// Because an absent rule already means hidden, hiding a resource that has no visible rule changes
/// nothing, audits nothing and emits nothing.
///
/// IDEMPOTENCY (docs/08_API_CONTRACTS.md). A client-supplied <c>Idempotency-Key</c> request HEADER is
/// REQUIRED; a repeated hide with the same key does not apply a second effect (the hide uses its own
/// per-tenant idempotency scope, distinct from reveal, so the same key value may pair a reveal with its
/// hide). Both a first apply and an idempotent retry return 200 — the command is idempotent and both leave
/// the resource hidden.
///
/// REALTIME EVENTS (<c>ContentHidden</c> + <c>VisibilityRuleChanged</c>). When — and only when — a hide
/// actually changes visibility (the same change signal the audit uses, so a retry or a no-op hide of an
/// already-hidden resource emits nothing), the command appends and delivers TWO durable events. The
/// <c>ContentHidden</c> event carries NO visibility subject: the resource is now hidden, so a subject-gated
/// projection would (correctly, for a reveal) exclude the audience that must be told to remove the
/// resource; instead it is routed by its coarse target — a selected-participant hide reaches only that
/// participant (plus hosts), an audience-wide hide reaches the observers and every active participant. The
/// <c>VisibilityRuleChanged</c> event (CORE-EVT-003, the security-relevant rule-change event, DISTINCT from
/// the audit record) CARRIES the resource as its visibility subject, so the recipient resolver gates it to
/// the HOSTS ONLY (the resource is now hidden, so no participant/observer may receive it) — the host-facing
/// delivery the catalog documents, with no leakage of a hidden resource. Both carry resource IDENTIFIERS
/// only, never resolved content (threats T2/T3/T7).
///
/// Tenant resolution + authorization mirror the reveal command (and the session start/end commands)
/// exactly: the route path carries only <c>{sessionId}</c>, the target organization is the body's
/// <c>organizationSlug</c> resolved by <see cref="TenantContextResolver"/> (token claim AND persisted
/// membership, threat T5), the session is loaded WITHIN the resolved tenant (org boundary leads), and the
/// caller is authorized by their role in the SESSION'S OWN workspace. Fail-closed at every step and never
/// leaking why:
/// <list type="bullet">
///   <item>503 when persistence is off; 401 when the principal cannot be mapped.</item>
///   <item>A malformed body or a missing <c>organizationSlug</c> is 400; a malformed session id, a
///   denied tenant resolution, a session not in the tenant, and a caller who is not a member of the
///   session's workspace are ALL hidden as 404.</item>
///   <item>A known workspace member who lacks the hide role (Owner/Admin/Host/CoHost — the same authz as
///   reveal, the "Execute reveal"/"Change visibility rule" rows of docs/06_AUTHORIZATION_MATRIX.md) is
///   403. <see cref="MembershipRole"/> is non-linear, so the role check is EXACT.</item>
///   <item>Only AFTER authorization is the rest of the request validated (so an unauthorized caller
///   never receives request-shape feedback): a missing <c>Idempotency-Key</c> header, an unknown or
///   numeric <c>resourceType</c>, and an empty <c>resourceId</c> are each 400.</item>
/// </list>
///
/// Persistence dependency: like the reveal endpoint, this uses the repositories, the tenant context
/// resolver and the System idempotency store, which are registered only when a database connection
/// string is configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed
/// with 503.
/// </summary>
internal static class HideEndpoints
{
    /// <summary>The request header carrying the client idempotency key (docs/08_API_CONTRACTS.md).</summary>
    private const string _idempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapHideEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid
        // token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        group.MapPost("/{sessionId}/hide", HideAsync);

        return endpoints;
    }

    // POST /api/v1/sessions/{sessionId}/hide
    private static async Task<IResult> HideAsync(
        HttpContext httpContext,
        string sessionId,
        [FromBody] HideRequest? request,
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
        // (this route has a body, like the reveal/workspace/scene create commands).
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
        // non-member is hidden as 404 (not 403), the same rule as the reveal/start/end commands.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenSession();
        }

        // The caller is a known member of the session's workspace, so an insufficient role is 403. The
        // hide roles are the same as reveal: Owner/Admin/Host/CoHost (docs/06_AUTHORIZATION_MATRIX.md).
        // MembershipRole is non-linear, so this is an EXACT set membership check.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized. Only now validate the rest of the request, so an unauthorized caller never
        // receives request-shape feedback.

        // The Idempotency-Key header is required for the hide command (docs/08_API_CONTRACTS.md).
        if (!TryGetIdempotencyKey(httpContext, out var idempotencyKey))
        {
            return ValidationError($"The '{_idempotencyKeyHeader}' header is required.");
        }

        // The resource kind is parsed by NAME; a numeric or unknown value is rejected (mirrors reveal).
        if (!TryParseResourceType(request.ResourceType, out var resourceType))
        {
            return ValidationError("The 'resourceType' value is not a recognized resource type.");
        }

        if (request.ResourceId == Guid.Empty)
        {
            return ValidationError("The 'resourceId' value is required.");
        }

        // Optional SELECTED-participant target (mirroring the selected reveal). Absent/null -> hide from
        // the whole audience. A present-but-empty id is a 400. A set id must be a participant of the
        // SESSION'S OWN workspace: it is resolved within the resolved tenant and its workspace is verified,
        // so a cross-tenant or cross-workspace (or unknown) participant is hidden as 404 — a host must not
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

        // ONE unit of work (CORE-CONC-002): the hide command's rule change, its append-only audit record and
        // its idempotency-key write — PLUS the append of both durable events the change emits — commit
        // together in a single database transaction. A part-way failure rolls them ALL back, so the
        // append-only event stream can never diverge from the persisted visibility state. Realtime DELIVERY
        // is held until AFTER the commit (commit-then-publish, below), so a delivery failure cannot roll
        // back committed state.
        var committed = await deps.UnitOfWork
            .ExecuteAsync(
                async transactionCancellationToken =>
                {
                    var result = await deps.Reveal
                        .HideAsync(
                            context.OrganizationId,
                            session.WorkspaceId,
                            session.Id,
                            resourceType,
                            request.ResourceId,
                            targetParticipantId,
                            context.UserProfileId,
                            idempotencyKey,
                            now,
                            transactionCancellationToken)
                        .ConfigureAwait(false);

                    // The durable events to deliver after the commit, appended here IFF the hide actually
                    // changed visibility — the same change signal the audit uses, so a retry or a no-op hide
                    // of an already-hidden resource appends and delivers nothing.
                    var events = new List<SessionEvent>();
                    if (result.VisibilityChanged)
                    {
                        var resourceTypeName = result.ResourceType.ToString();

                        // CONTENT-HIDDEN EVENT: appended to the session's append-only stream here (inside the
                        // transaction) and delivered after the commit. It carries NO visibility subject (see
                        // the type summary): the resource is now hidden, so it is routed by its coarse target
                        // instead — a selected-participant hide reaches only that participant (plus hosts), an
                        // audience-wide hide reaches the observers and every active participant — so everyone
                        // who could be showing the resource is told to remove it. The payload carries resource
                        // IDENTIFIERS only, never resolved content (threats T2/T3/T7).
                        var payload = JsonSerializer.Serialize(new HideEventPayload(
                            resourceTypeName,
                            result.ResourceId));
                        var contentHidden = SessionEvent.Create(
                            context.OrganizationId,
                            session.WorkspaceId,
                            sessionGuid,
                            SessionEventTypes.ContentHidden,
                            context.UserProfileId,
                            targetParticipantId,
                            payload,
                            schemaVersion: 1,
                            now);
                        await deps.EventPublisher.AppendAsync(contentHidden, transactionCancellationToken).ConfigureAwait(false);
                        events.Add(contentHidden);

                        // VISIBILITY-RULE-CHANGED EVENT (CORE-EVT-003): the rule's new state is Hidden, so
                        // append the durable VisibilityRuleChanged session event (the realtime counterpart of
                        // the audit record, DISTINCT from it) reusing the SAME composer as the reveal
                        // endpoint. Unlike ContentHidden it CARRIES the resource as its visibility subject:
                        // the resource is now hidden, so the recipient resolver gates this event to the HOSTS
                        // ONLY — and no participant ever receives a hidden-resource event (threats T2/T3).
                        events.Add(await RevealEndpoints.AppendVisibilityRuleChangedAsync(
                            deps.EventPublisher,
                            context.OrganizationId,
                            session.WorkspaceId,
                            sessionGuid,
                            context.UserProfileId,
                            targetParticipantId,
                            resourceTypeName,
                            result.ResourceId,
                            VisibilityState.Hidden,
                            now,
                            transactionCancellationToken)
                            .ConfigureAwait(false));
                    }

                    return new HideCommitResult(result, events);
                },
                cancellationToken)
            .ConfigureAwait(false);

        // COMMIT-THEN-PUBLISH (CORE-CONC-002): the transaction has committed, so deliver each appended event
        // to its server-computed realtime recipients OUTSIDE the transaction. Delivery is best-effort, so a
        // delivery failure cannot roll back the committed hide; a reconnecting client replays a missed push
        // later (CORE-RT-005). The recipient resolver still gates every delivery through the central
        // Visibility engine (threats T2/T3).
        foreach (var sessionEvent in committed.Events)
        {
            await deps.EventPublisher.DeliverAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        }

        return Results.Ok(HideResponse.From(committed.Result));
    }

    /// <summary>
    /// The outcome of the hide unit of work: the command <see cref="HideResult"/> the response is built
    /// from, plus the durable events that were APPENDED inside the transaction and must be DELIVERED after
    /// the commit (commit-then-publish, CORE-CONC-002). The list is empty when the hide changed nothing.
    /// </summary>
    private readonly record struct HideCommitResult(HideResult Result, IReadOnlyList<SessionEvent> Events);

    /// <summary>
    /// The server-composed payload of a <c>ContentHidden</c> event: the generic kind and id of the hidden
    /// resource. Identifiers only — never the resolved content (threat T7). Mirrors the reveal payload.
    /// </summary>
    private sealed record HideEventPayload(string ResourceType, Guid ResourceId);

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
    /// (mirrors the reveal endpoint).
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

    private static bool TryGetDependencies(HttpContext httpContext, out HideEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var sessions = services.GetService<ISessionRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var participants = services.GetService<IParticipantRepository>();
        var reveal = services.GetService<RevealService>();
        var eventPublisher = services.GetService<ISessionEventPublisher>();
        var unitOfWork = services.GetService<TransactionalUnitOfWork>();

        if (resolver is null
            || sessions is null
            || workspaceMembers is null
            || participants is null
            || reveal is null
            || eventPublisher is null
            || unitOfWork is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new HideEndpointDependencies(
            resolver, sessions, workspaceMembers, participants, reveal, eventPublisher, unitOfWork);
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
            detail: "The hide command requires persistence, which is not configured.");

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

    private readonly record struct HideEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        IParticipantRepository Participants,
        RevealService Reveal,
        ISessionEventPublisher EventPublisher,
        TransactionalUnitOfWork UnitOfWork);
}
