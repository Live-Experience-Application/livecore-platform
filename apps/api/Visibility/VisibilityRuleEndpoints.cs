// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Visibility;

/// <summary>
/// HTTP endpoints of the Visibility module's visibility-rule authoring API (CORE-SVIS-005, the "Vertical
/// Adopter Consumability Completeness" epic). They let an authoring role CREATE, LIST and READ
/// <see cref="VisibilityRule"/>s for a session's resources over HTTP, so a vertical can configure
/// session-scoped visibility through the API and drive the reveal/visibility engine — a capability that was
/// deliberately-absent (docs/24_SPEC_CONSISTENCY.md) until this story. They realize the documented request
/// flow (authentication -&gt; tenant/workspace context resolver -&gt; endpoint -&gt; authorization policy,
/// docs/02_ARCHITECTURE.md), mirroring the reveal/hide command endpoints (<see cref="RevealEndpoints"/>)
/// exactly — the same Visibility module, the same session-scoped tenant resolution and the same fail-closed,
/// hidden-404 authorization.
///
/// Routes owned by this story, all under <c>/api/v1/sessions/{sessionId}/visibility-rules</c>
/// (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>POST /api/v1/sessions/{sessionId}/visibility-rules</c> — module <b>Visibility</b>, roles
///   "Host,CoHost,Owner,Admin". Creates a visibility rule for a resource in the session via
///   <see cref="VisibilityRuleService"/>: the referenced resource is resolved WITHIN the session's workspace
///   (the same-workspace invariant the database foreign key cannot enforce — the headline requirement), the
///   rule is created with a SERVER-MINTED id and the creation is appended to the append-only audit log
///   (<see cref="Audit.AuditAction.VisibilityRuleChanged"/>), the insert and the audit committing together
///   in one transaction. The organization slug travels in the request body (like reveal/hide). Returns
///   <c>201 Created</c> with the host <see cref="VisibilityRuleResponse"/>.</item>
///   <item><c>GET /api/v1/sessions/{sessionId}/visibility-rules</c> — module Visibility, roles
///   "Host,CoHost,Owner,Admin". Lists the session's rules (both the audience-wide and the
///   selected-participant dimensions) in the deterministic (UUIDv7) id order as a bounded page
///   (CORE-DX-003). A visibility rule is an AUTHORING artifact (a host configures it), so — like the
///   entity-type list — it is restricted to the authoring roles and there is NO host-vs-participant
///   projection: a participant cannot ENUMERATE rules.</item>
///   <item><c>GET /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}</c> — module Visibility, roles
///   "Host,CoHost,Owner,Admin". Reads ONE rule within the route's session via
///   <see cref="IVisibilityRuleRepository.FindByIdInSessionAsync"/> (tenant-, workspace- AND session-scoped),
///   so a rule of another session, workspace or tenant is never returned even when its surrogate id is known;
///   a foreign/unknown rule is a hidden <c>404</c>.</item>
///   <item><c>POST /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/lock</c> and
///   <c>.../unlock</c> — module Visibility, roles "Host,CoHost,Owner,Admin" (CORE-VSEAL-001). SEAL (lock) or
///   unseal (unlock) a rule via <see cref="VisibilityRuleLockService"/>, asserting the server-side flag that
///   makes the governed resource permanently-restricted: while a rule is locked a reveal/hide/change targeting
///   it is refused fail-closed (<c>409</c>). Only the authoring roles may set or clear the lock; the change is
///   audited (<see cref="Audit.AuditAction.VisibilityRuleLockChanged"/>) and idempotent (re-locking/unlocking
///   is a no-op). The organization slug travels as a required query parameter (these commands carry no body).
///   Returns <c>200 OK</c> with the host <see cref="VisibilityRuleResponse"/> carrying the updated
///   <c>locked</c> flag; a foreign/unknown/cross-session rule is a hidden <c>404</c>.</item>
/// </list>
///
/// Tenant resolution + authorization mirror the reveal command exactly: the route path carries only
/// <c>{sessionId}</c>; the target organization is the body's <c>organizationSlug</c> (create) or a required
/// <c>organizationSlug</c> QUERY parameter (the GET reads), resolved by <see cref="TenantContextResolver"/>
/// (token claim AND persisted membership, threat T5); the session is loaded WITHIN the resolved tenant (org
/// boundary leads), its own workspace is discovered from the loaded row, and the caller is authorized by
/// their role in the SESSION'S OWN workspace. Fail-closed at every step and never leaking why:
/// <list type="bullet">
///   <item>503 when persistence is off; 401 when the principal cannot be mapped.</item>
///   <item>A missing <c>organizationSlug</c> (or, for create, a missing body) is 400; a malformed session id,
///   a denied tenant resolution, a session not in the tenant, and a caller who is not a member of the
///   session's workspace are ALL hidden as 404 (never distinguishable, never 403 for a non-member; threats
///   T1/T5).</item>
///   <item>A known workspace member who lacks the authoring role (Owner/Admin/Host/CoHost — the "Change
///   visibility rule" row of docs/06_AUTHORIZATION_MATRIX.md) is 403 on ALL THREE routes: a participant can
///   neither author NOR enumerate rules. <see cref="MembershipRole"/> is non-linear, so the role check is
///   EXACT, never an ordering comparison.</item>
///   <item>For create, only AFTER authorization is the rest of the request validated (so an unauthorized
///   caller never receives request-shape feedback): an unknown/numeric <c>resourceType</c> or
///   <c>visibility</c>, an empty <c>resourceId</c>, and a present-but-empty <c>participantId</c> are each
///   400. A referenced resource that is not in the session's workspace is a 400 (the same-workspace invariant
///   rejection); a target participant outside the workspace is hidden as 404; a rule that already exists for
///   the same (session, resource, dimension) is 409.</item>
/// </list>
///
/// Persistence dependency: like the reveal endpoint, this uses the repositories, the tenant context resolver
/// and the create service, which are registered only when a database connection string is configured (see
/// <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class VisibilityRuleEndpoints
{
    /// <summary>Required query parameter naming the target organization (the GET reads).</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapVisibilityRuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        group.MapPost("/{sessionId}/visibility-rules", CreateVisibilityRuleAsync);
        group.MapGet("/{sessionId}/visibility-rules", ListVisibilityRulesAsync);
        group.MapGet("/{sessionId}/visibility-rules/{ruleId}", GetVisibilityRuleByIdAsync);
        group.MapPost("/{sessionId}/visibility-rules/{ruleId}/lock", LockVisibilityRuleAsync);
        group.MapPost("/{sessionId}/visibility-rules/{ruleId}/unlock", UnlockVisibilityRuleAsync);

        return endpoints;
    }

    // POST /api/v1/sessions/{sessionId}/visibility-rules
    private static async Task<IResult> CreateVisibilityRuleAsync(
        HttpContext httpContext,
        string sessionId,
        [FromBody] CreateVisibilityRuleRequest? request,
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

        // A missing/unparseable body cannot carry the target organization or resource; 400. (Malformed JSON
        // is rejected as 400 by the framework before the handler.)
        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        // The target organization is required to resolve the tenant; it is supplied in the body (this route
        // has a body, like the reveal/hide commands).
        if (string.IsNullOrWhiteSpace(request.OrganizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session id can never address a stored session; hidden as 404.
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenRule();
        }

        var (authorized, context, session, failure) = await AuthorizeAsync(
                deps, principal, request.OrganizationSlug, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (!authorized)
        {
            return failure!;
        }

        // Authorized. Only now validate the rest of the request, so an unauthorized caller never receives
        // request-shape feedback (the reveal rule).

        // The resource kind is parsed by NAME; a numeric or unknown value is rejected (mirrors the reveal
        // command's resource-type parsing).
        if (!TryParseResourceType(request.ResourceType, out var resourceType))
        {
            return ValidationError("The 'resourceType' value is not a recognized resource type.");
        }

        if (request.ResourceId == Guid.Empty)
        {
            return ValidationError("The 'resourceId' value is required.");
        }

        // The base audience visibility is parsed by NAME; a numeric or unknown value is rejected.
        if (!TryParseVisibility(request.Visibility, out var visibility))
        {
            return ValidationError("The 'visibility' value is not a recognized visibility state.");
        }

        // Optional SELECTED-participant target (CORE-VIS-005). Absent/null -> an audience-wide rule. A
        // present-but-empty id is a 400 (it can never address a participant). A set id is resolved within the
        // session's workspace by the create service (a cross-workspace/unknown participant is hidden as 404).
        if (request.ParticipantId == Guid.Empty)
        {
            return ValidationError("The 'participantId' value must not be empty.");
        }

        var now = timeProvider.GetUtcNow();
        var result = await deps.VisibilityRules
            .CreateAsync(
                context!.OrganizationId,
                session!.WorkspaceId,
                session.Id,
                resourceType,
                request.ResourceId,
                visibility,
                request.ParticipantId,
                context.UserProfileId,
                now,
                cancellationToken,
                // Optional scheduled-reveal time (CORE-VSEAL-002): the worker's sweep auto-reveals a Hidden rule
                // once this time arrives, through the central reveal engine. Null leaves the rule unscheduled.
                request.ScheduledRevealAt)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            // The referenced resource is not in the session's workspace (an unknown id, or one belonging to
            // another workspace/tenant — indistinguishable): the same-workspace invariant rejection. A 400,
            // exactly as the entity create rejects an unresolved type reference (the body-supplied resource
            // reference does not resolve in the caller's authorized workspace; leaks nothing, threats T1/T5).
            case VisibilityRuleCreationStatus.ResourceNotInWorkspace:
                return ValidationError("The referenced resource does not exist in the session's workspace.");

            // The selected-participant target is not in the session's workspace: hidden as 404, exactly as the
            // reveal command hides a cross-workspace participant target (threat T5).
            case VisibilityRuleCreationStatus.ParticipantNotInWorkspace:
                return HiddenRule();

            // A rule already exists for the same (session, resource, dimension): 409 duplicate (CORE-SVIS-002).
            case VisibilityRuleCreationStatus.Duplicate:
                return DuplicateRule();

            default:
                // Resolve the created rule's governed resource to its audience-safe label so the create
                // response carries the same denormalized name the list/read rows do (CORE-APROJ-004). The
                // resource was just verified to live in the session's workspace, so this normally resolves;
                // it still degrades to a null label without error if it cannot.
                var createdRule = result.Rule!;
                var createdLabel = await ResolveResourceLabelAsync(
                        deps, context!.OrganizationId, session!.WorkspaceId, createdRule, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Created(
                    $"/api/v1/sessions/{session.Id}/visibility-rules/{createdRule.Id}",
                    VisibilityRuleResponse.From(createdRule, createdLabel));
        }
    }

    // GET /api/v1/sessions/{sessionId}/visibility-rules?organizationSlug={slug}&limit={n}&offset={n}
    private static async Task<IResult> ListVisibilityRulesAsync(
        HttpContext httpContext,
        string sessionId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        [FromQuery(Name = Page.LimitQuery)] string? limit,
        [FromQuery(Name = Page.OffsetQuery)] string? offset,
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

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenRule();
        }

        var (authorized, context, session, failure) = await AuthorizeAsync(
                deps, principal, organizationSlug, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (!authorized)
        {
            return failure!;
        }

        // Authorized. Only now validate the optional paging parameters, so a non-authoring caller never
        // receives request-shape feedback (the audit read's rule): a present-but-malformed limit/offset is a
        // 400, an absent value uses the default page.
        if (!Page.TryResolveLimit(limit, out var pageLimit, out var limitError))
        {
            return ValidationError(limitError);
        }

        if (!Page.TryResolveOffset(offset, out var pageOffset, out var offsetError))
        {
            return ValidationError(offsetError);
        }

        // BOUNDED (CORE-DX-003): the page is read through the tenant-, workspace- and session-scoped paged
        // repository, fetching ONE extra row so HasMore is set without a second COUNT; the trimmed page is then
        // projected. A list can never return the whole table (threat T9). The rules of a concurrent session of
        // the same workspace are never enumerated here (session-scoped; threat T5/T3).
        var rows = await deps.Rules
            .ListPageBySessionAsync(context!.OrganizationId, session!.WorkspaceId, session.Id, pageOffset, pageLimit + 1, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > pageLimit;
        var pageRows = hasMore ? rows.Take(pageLimit).ToArray() : rows;

        // Name each row from the rule list ALONE (CORE-APROJ-004): resolve every governed resource to its
        // denormalized, audience-safe label through the Visibility-owned port, so a host can render a
        // per-resource visibility matrix with NO per-resource host read. The per-row resolution is bounded by
        // the (bounded) page; a rule whose resource no longer resolves in the session's workspace — a dangling
        // rule — degrades to a null label without error. The label is the resource's audience-safe NAME, never
        // its host content (threats T2/T7).
        var items = new VisibilityRuleResponse[pageRows.Count];
        for (var index = 0; index < pageRows.Count; index++)
        {
            var rule = pageRows[index];
            var label = await ResolveResourceLabelAsync(
                    deps, context.OrganizationId, session.WorkspaceId, rule, cancellationToken)
                .ConfigureAwait(false);
            items[index] = VisibilityRuleResponse.From(rule, label);
        }

        return Results.Ok(PageResponse.From<VisibilityRuleResponse>(items, pageOffset, pageLimit, hasMore));
    }

    // GET /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}?organizationSlug={slug}
    private static async Task<IResult> GetVisibilityRuleByIdAsync(
        HttpContext httpContext,
        string sessionId,
        string ruleId,
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

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session or rule id can never address a stored row; treat each as hidden (404).
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenRule();
        }

        if (!Guid.TryParse(ruleId, out var ruleGuid) || ruleGuid == Guid.Empty)
        {
            return HiddenRule();
        }

        var (authorized, context, session, failure) = await AuthorizeAsync(
                deps, principal, organizationSlug, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (!authorized)
        {
            return failure!;
        }

        // Load the rule through the tenant-, workspace- AND session-scoped FindByIdInSessionAsync (it leads
        // with organization_id then workspace_id then session_id then the rule id), so a rule in another
        // session, workspace or tenant is never returned even when the surrogate id matches; an unknown id is
        // a hidden 404 (threats T1/T5; the cross-session leak T3).
        var rule = await deps.Rules
            .FindByIdInSessionAsync(context!.OrganizationId, session!.WorkspaceId, session.Id, ruleGuid, cancellationToken)
            .ConfigureAwait(false);
        if (rule is null)
        {
            return HiddenRule();
        }

        // Name the row from the rule alone (CORE-APROJ-004): resolve the governed resource to its
        // denormalized, audience-safe label through the Visibility-owned port; a dangling rule degrades to a
        // null label without error.
        var label = await ResolveResourceLabelAsync(
                deps, context.OrganizationId, session.WorkspaceId, rule, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(VisibilityRuleResponse.From(rule, label));
    }

    // POST /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/lock?organizationSlug={slug}
    private static Task<IResult> LockVisibilityRuleAsync(
        HttpContext httpContext,
        string sessionId,
        string ruleId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => SetLockAsync(httpContext, sessionId, ruleId, organizationSlug, locked: true, timeProvider, cancellationToken);

    // POST /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}/unlock?organizationSlug={slug}
    private static Task<IResult> UnlockVisibilityRuleAsync(
        HttpContext httpContext,
        string sessionId,
        string ruleId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => SetLockAsync(httpContext, sessionId, ruleId, organizationSlug, locked: false, timeProvider, cancellationToken);

    /// <summary>
    /// Shared handler behind the lock and unlock commands (CORE-VSEAL-001): they differ ONLY in the
    /// <paramref name="locked"/> direction, so they share one fail-closed flow — the same tenant resolution and
    /// authoring authorization as the create/read routes (<see cref="AuthorizeAsync"/>), then the
    /// <see cref="VisibilityRuleLockService"/> effect. The organization slug is a required QUERY parameter (a
    /// lock command carries no body); a missing slug is 400, a malformed session/rule id is hidden as 404, and
    /// a non-authoring member is 403 exactly like the other rule routes. The rule is loaded and sealed/unsealed
    /// SESSION-SCOPED, so a foreign/unknown/cross-session rule is hidden as 404 (threats T1/T5/T3). On success
    /// the response carries the updated rule (with its <c>locked</c> flag) and the audience-safe resource label.
    /// </summary>
    private static async Task<IResult> SetLockAsync(
        HttpContext httpContext,
        string sessionId,
        string ruleId,
        string? organizationSlug,
        bool locked,
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

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session or rule id can never address a stored row; treat each as hidden (404).
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenRule();
        }

        if (!Guid.TryParse(ruleId, out var ruleGuid) || ruleGuid == Guid.Empty)
        {
            return HiddenRule();
        }

        var (authorized, context, session, failure) = await AuthorizeAsync(
                deps, principal, organizationSlug, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (!authorized)
        {
            return failure!;
        }

        // SEAL/UNSEAL the rule WITHIN the resolved tenant, workspace and session: the service loads the rule
        // session-scoped and atomically audits a real lock transition. A rule of another session, workspace or
        // tenant — or an unknown rule — is never reachable, so it is hidden as 404 (threats T1/T5/T3). The
        // command is idempotent (re-locking/unlocking is a no-op).
        var now = timeProvider.GetUtcNow();
        var rule = await deps.LockService
            .SetLockAsync(
                context!.OrganizationId, session!.WorkspaceId, session.Id, ruleGuid, locked, context.UserProfileId, now, cancellationToken)
            .ConfigureAwait(false);
        if (rule is null)
        {
            return HiddenRule();
        }

        // Name the row from the rule alone (CORE-APROJ-004): resolve the governed resource to its
        // denormalized, audience-safe label through the Visibility-owned port; a dangling rule degrades to a
        // null label without error. The projection carries the updated locked flag (CORE-VSEAL-001).
        var label = await ResolveResourceLabelAsync(
                deps, context.OrganizationId, session.WorkspaceId, rule, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(VisibilityRuleResponse.From(rule, label));
    }

    /// <summary>
    /// Resolves a rule's governed resource to its denormalized, AUDIENCE-SAFE label (CORE-APROJ-004) through
    /// the Visibility-owned <see cref="IVisibleResourceAudienceProjector"/> port (CORE-APROJ-002) — the same
    /// hexagonal same-workspace resource port family CORE-SVIS-005 introduced, whose composition-root adapter
    /// consults the owning resource module's AUDIENCE projection. The label is the resource's audience-safe
    /// NAME (a scene's title, an entity's name, a content block's generic kind), never its full/host content,
    /// so it can never leak host-only content even to an authoring caller (threats T2/T7). The lookup is the
    /// rule's OWN (organization, workspace), so a resource outside the rule's workspace is never read; a rule
    /// whose resource no longer resolves — a dangling rule — yields <see langword="null"/> WITHOUT error. The
    /// central security module never references the Scenes/Content/Entities modules directly (CORE-ARCH-001).
    /// </summary>
    private static async Task<string?> ResolveResourceLabelAsync(
        VisibilityRuleEndpointDependencies deps,
        Guid organizationId,
        Guid workspaceId,
        VisibilityRule rule,
        CancellationToken cancellationToken)
    {
        var projection = await deps.AudienceProjector
            .ProjectAudienceSafeAsync(organizationId, workspaceId, rule.ResourceType, rule.ResourceId, cancellationToken)
            .ConfigureAwait(false);

        // The audience-safe label is the resource's audience title (scene title / content-block kind / entity
        // name); the audience body is intentionally not part of a rule row's projection.
        return projection?.Title;
    }

    /// <summary>
    /// The shared tenant-resolution + object-level authorization the three routes perform: resolve the tenant
    /// from the supplied organization slug, load the session WITHIN the tenant (org boundary leads), discover
    /// the session's own workspace, load the caller's membership in that workspace, and require the AUTHORING
    /// role set (Owner/Admin/Host/CoHost — the "Change visibility rule" row of docs/06). Fail-closed and
    /// indistinguishable: a denied tenant, an unknown/cross-tenant session and a non-member of the session's
    /// workspace are all hidden as 404; a known member lacking the authoring role is 403. On success returns
    /// the resolved tenant context and the loaded session; otherwise the failure result to return.
    /// </summary>
    private static async Task<(bool Authorized, TenantContext? Context, Session? Session, IResult? Failure)> AuthorizeAsync(
        VisibilityRuleEndpointDependencies deps,
        OidcPrincipal principal,
        string organizationSlug,
        Guid sessionGuid,
        CancellationToken cancellationToken)
    {
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (threat T5).
            return (false, default, null, HiddenRule());
        }

        var context = resolution.Context;

        // Load the session WITHIN the resolved tenant (org boundary leads); a cross-tenant or unknown session
        // is hidden as 404. The session's workspace is then discovered from the loaded row.
        var session = await deps.Sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return (false, default, null, HiddenRule());
        }

        // Object-level authorization: the caller must be a member of the SESSION'S workspace; a non-member is
        // hidden as 404 (not 403), the same rule as the reveal command (threats T1/T5).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return (false, default, null, HiddenRule());
        }

        // The caller is a known member of the session's workspace, so an insufficient role is 403. Authoring a
        // visibility rule — and ENUMERATING rules — is the "Change visibility rule" capability
        // (Owner/Admin/Host/CoHost; docs/06_AUTHORIZATION_MATRIX.md), so a participant can neither author nor
        // enumerate them. MembershipRole is non-linear, so this is an EXACT set membership check.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return (false, default, null, Forbidden());
        }

        return (true, context, session, null);
    }

    /// <summary>
    /// Parses a <see cref="VisibilityResourceType"/> from its NAME only, rejecting null/blank, numeric values
    /// and unknown names — so a client cannot smuggle in an undefined enum value by number (mirrors the reveal
    /// command's resource-type parsing).
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

    /// <summary>
    /// Parses a <see cref="VisibilityState"/> from its NAME only, rejecting null/blank, numeric values and
    /// unknown names — so a client cannot smuggle in an undefined enum value by number.
    /// </summary>
    private static bool TryParseVisibility(string? value, out VisibilityState visibility)
    {
        visibility = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (int.TryParse(value, out _))
        {
            return false;
        }

        return Enum.TryParse(value, ignoreCase: false, out visibility)
            && VisibilityRule.IsValidVisibility(visibility);
    }

    private static bool TryGetDependencies(HttpContext httpContext, out VisibilityRuleEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var sessions = services.GetService<ISessionRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var visibilityRules = services.GetService<VisibilityRuleService>();
        var lockService = services.GetService<VisibilityRuleLockService>();
        var rules = services.GetService<IVisibilityRuleRepository>();
        var audienceProjector = services.GetService<IVisibleResourceAudienceProjector>();

        if (resolver is null
            || sessions is null
            || workspaceMembers is null
            || visibilityRules is null
            || lockService is null
            || rules is null
            || audienceProjector is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new VisibilityRuleEndpointDependencies(
            resolver, sessions, workspaceMembers, visibilityRules, lockService, rules, audienceProjector);
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
        => CoreProblem.Create(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            code: ProblemCodes.ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Visibility rule operations require persistence, which is not configured.");

    private static IResult Unauthorized()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status401Unauthorized,
            code: ProblemCodes.AuthenticationRequired,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult Forbidden()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status403Forbidden,
            code: ProblemCodes.PermissionDenied,
            title: "Forbidden",
            detail: "You are not authorized to perform this action.");

    private static IResult MissingOrganization()
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

    // A rule already exists for the same (session, resource, dimension): a uniqueness conflict (CORE-SVIS-002).
    // The caller is authorized; the existing rule, not the caller, is the reason, so this is a 409 with the
    // duplicate-resource code. The detail names only the generic conflict and leaks no tenant data (threat T7).
    private static IResult DuplicateRule()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.DuplicateResource,
            title: "Conflict",
            detail: "A visibility rule already exists for this resource in this dimension.");

    // Rule/session existence is hidden: a malformed id, a session in a foreign or non-entitled tenant, an
    // unknown session, a session in a workspace the caller does not belong to, a target participant outside
    // the workspace, and a foreign/unknown rule are ALL reported as 404, never distinguishable and never
    // echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenRule() => NotFound();

    private static IResult NotFound()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct VisibilityRuleEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        VisibilityRuleService VisibilityRules,
        VisibilityRuleLockService LockService,
        IVisibilityRuleRepository Rules,
        IVisibleResourceAudienceProjector AudienceProjector);
}
