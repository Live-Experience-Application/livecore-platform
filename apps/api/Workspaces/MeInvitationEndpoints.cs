// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// HTTP endpoint of the user-scoped pending-invitation self-discovery read (CORE-INV-002,
/// <c>GET /api/v1/me/invitations</c>). It is the onboarding-discovery half of the workspace invite flow: the
/// host already invites by email (CORE-WS-004) and the authenticated caller already redeems a scoped token
/// (CORE-WS-006), but an invitee had no way to FIND an invitation addressed to them — they needed the host to
/// hand over the workspace id out of band. This route lets the authenticated caller discover their own PENDING
/// invitations so an onboarding flow can then drive the existing
/// <c>POST /api/v1/workspaces/{workspaceId}/invitations/accept</c> with no out-of-band coordination and without
/// enumerating workspaces (the vertical-adopter gap ARC-GAP-101).
///
/// It is mounted under the existing <c>/me</c> family (mirroring the
/// <c>/me</c>, <c>/me/entitlements</c>, <c>/me/quota-status</c> and <c>/me/ad-eligibility</c> reads) and follows
/// their fail-closed pattern exactly. It lives in the Workspaces module because it reads workspace invitations
/// (the <see cref="WorkspaceInvitation"/> aggregate), using the IdentityAccess principal/profile and the
/// Organizations tenant reads — both allowed module edges (docs/05_MODULE_CONTRACTS.md).
///
/// THE LOAD-BEARING SECURITY RULE — matching is ONLY ever on the caller's trustworthy VERIFIED email
/// (CORE-INV-001). Invitations are keyed by a raw invited-email string plus a token hash with no user-id link,
/// so the only safe server-side key is the caller's verified email. Keying on the bare, spoofable
/// <see cref="OidcPrincipal.Email"/> would be an email-enumeration / invitation-hijack vector, so this route
/// keys ONLY on <see cref="OidcPrincipal.EmailVerified"/>-backed email (threats T5/T6 in
/// docs/07_SECURITY_THREAT_MODEL.md). A caller with no verified email gets an EMPTY list — never an error,
/// never a guess.
///
/// Authorization model (object-level, server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md; threats
/// T1/T5/T6/T7):
/// <list type="bullet">
///   <item>The bearer middleware challenges a missing/invalid token with 401 before any handler runs; a failed
///   principal mapping is 401.</item>
///   <item><c>/me/invitations</c> is a USER concept: a service-account principal holds no profile, no membership
///   and no verified email, so it is denied 403 — the same rule the sibling <c>/me</c> routes apply.</item>
///   <item>The result is scoped to the caller's CLAIMED tenants: the organizations the token asserts an
///   organization claim for that exist — the SAME token-claim scope the accept route resolves against through
///   its distinct claim-only path (<see cref="TenantContextResolver.ResolveClaimScopedAsync"/>), NOT a
///   pre-existing membership (CORE-INV-003). An invitation is therefore discoverable in any tenant the caller
///   can actually resolve and accept against, so a brand-new or cross-org invitee — who has no membership yet —
///   discovers and accepts their invitation; an invitation in a tenant the token does not claim is never
///   returned (threat T5). Matching within that scope is still ONLY ever the caller's verified email, so a token
///   claim never surfaces another person's invitation.</item>
///   <item>The projection (<see cref="MyPendingWorkspaceInvitationResponse"/>) carries the organization slug,
///   workspace id, role, status and expiry — the fields the accept flow needs — and NEVER the token hash, the
///   one-time token (which does not exist on a stored invitation) nor any person's data, not even the caller's
///   own invited email (threats T6/T7).</item>
/// </list>
///
/// Persistence dependency: like the sibling <c>/me</c> reads, the handler uses the organization repository and
/// the workspace-invitation repository, registered only when a database connection string is configured (see
/// <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class MeInvitationEndpoints
{
    public static IEndpointRouteBuilder MapMeInvitationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1")
            .RequireAuthorization();

        group.MapGet("/me/invitations", ListMyPendingInvitationsAsync);

        return endpoints;
    }

    // GET /api/v1/me/invitations?limit={n}&offset={n}
    private static async Task<IResult> ListMyPendingInvitationsAsync(
        HttpContext httpContext,
        [FromQuery(Name = Page.LimitQuery)] string? limit,
        [FromQuery(Name = Page.OffsetQuery)] string? offset,
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

        // /me is inherently a user concept: a service account has no user profile, no membership and no verified
        // email, so it is denied fail-closed (403), exactly as the sibling /me reads deny a service account.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Validate the optional paging parameters up front for every authenticated user (a present-but-malformed
        // value is a 400; an absent value uses the default page). There is no resource existence to hide on a /me
        // route, so validating the request shape here leaks nothing (threat T7).
        if (!Page.TryResolveLimit(limit, out var pageLimit, out var limitError))
        {
            return ValidationError(limitError);
        }

        if (!Page.TryResolveOffset(offset, out var pageOffset, out var offsetError))
        {
            return ValidationError(offsetError);
        }

        // FAIL CLOSED on the verified-email key (CORE-INV-001/002): matching is ONLY ever on the caller's
        // trustworthy verified email. A caller with no verified email (a missing, unverified or spoofable email)
        // gets an EMPTY page — never an error, never a guess — because keying on a bare unverified email would be
        // an email-enumeration / invitation-hijack vector (threats T5/T6). The model invariant guarantees
        // EmailVerified implies a present Email, so Email is non-null below. The short-circuit also avoids
        // touching the database for a caller who can never match anything.
        if (!principal.EmailVerified)
        {
            return Results.Ok(EmptyPage(pageOffset, pageLimit));
        }

        // The caller's CLAIMED tenants (CORE-INV-003): the organizations the token asserts an organization claim
        // for that exist — NO persisted membership required. This is the SAME token-claim scope the accept route
        // resolves against (the distinct claim-only path), so a brand-new or cross-org invitee — who has no
        // membership yet, and who the previous membership-AND-claim intersection left with a dead, empty list —
        // discovers an invitation addressed to them and can then accept it. The match below is still ONLY ever the
        // caller's VERIFIED email, so a token claim never surfaces another person's invitation. A claim value that
        // is not even slug-shaped, or that names no organization, is simply skipped (never an error); matching the
        // organization is exact (canonical, ordinal) so a near-match claim never crosses the tenant boundary
        // (threat T5).
        var claimedTenantsById = new Dictionary<Guid, Organization>();
        foreach (var organizationClaim in principal.OrganizationClaims)
        {
            string canonicalSlug;
            try
            {
                canonicalSlug = Organization.CanonicalizeSlug(organizationClaim);
            }
            catch (ArgumentException)
            {
                // A claim value that is not even a valid slug shape can name no tenant; skip it.
                continue;
            }

            var organization = await deps.Organizations
                .FindBySlugAsync(canonicalSlug, cancellationToken)
                .ConfigureAwait(false);
            if (organization is not null)
            {
                claimedTenantsById[organization.Id] = organization;
            }
        }

        // No claimed tenant resolves to a real organization: nothing the caller can be invited into and accept
        // against, so an empty page.
        if (claimedTenantsById.Count == 0)
        {
            return Results.Ok(EmptyPage(pageOffset, pageLimit));
        }

        // Read ONE BOUNDED PAGE of the Pending invitations addressed to the caller's VERIFIED email across the
        // claimed tenants, oldest first. Over-fetch one extra row so HasMore is set without a second COUNT; the
        // bound means a single response can never materialize an unbounded array (threat T9).
        var rows = await deps.WorkspaceInvitations
            .ListPendingPageByInvitedEmailInOrganizationsAsync(
                claimedTenantsById.Keys.ToArray(),
                principal.Email!,
                pageOffset,
                pageLimit + 1,
                cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > pageLimit;
        var pageRows = hasMore ? rows.Take(pageLimit) : rows;

        // Exclude an expired-but-still-Pending invitation: it can no longer be redeemed, so it must not be listed
        // (the same redeemability gate the accept route enforces). The expiry comparison runs here, not in SQL,
        // because the SQLite test provider cannot compare a DateTimeOffset. The organization slug is taken from
        // the claimed-tenant map (the aggregate does not carry it) — and the map only holds tenants the caller is
        // entitled to, so a returned invitation always carries a slug the caller can act on.
        var now = timeProvider.GetUtcNow();
        var items = pageRows
            .Where(invitation => invitation.IsRedeemable(now))
            .Select(invitation => MyPendingWorkspaceInvitationResponse.From(
                invitation, claimedTenantsById[invitation.OrganizationId].Slug))
            .ToArray();

        return Results.Ok(PageResponse.From(items, pageOffset, pageLimit, hasMore));
    }

    private static PageResponse<MyPendingWorkspaceInvitationResponse> EmptyPage(int offset, int limit)
        => PageResponse.From(Array.Empty<MyPendingWorkspaceInvitationResponse>(), offset, limit, hasMore: false);

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out MeInvitationEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var organizations = services.GetService<IOrganizationRepository>();
        var invitations = services.GetService<IWorkspaceInvitationRepository>();

        if (organizations is null || invitations is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new MeInvitationEndpointDependencies(organizations, invitations);
        return true;
    }

    /// <summary>
    /// Maps the authenticated principal to an <see cref="OidcPrincipal"/>, fail-closed: a failed mapping yields
    /// <see langword="false"/> (the caller returns 401). The mapping error reason is never echoed (threat T7).
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
            detail: "Invitation discovery requires persistence, which is not configured.");

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

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

    private readonly record struct MeInvitationEndpointDependencies(
        IOrganizationRepository Organizations,
        IWorkspaceInvitationRepository WorkspaceInvitations);
}
