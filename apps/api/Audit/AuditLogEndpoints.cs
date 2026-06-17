// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Audit;

/// <summary>
/// HTTP endpoint of the Audit module's view-audit-log read (CORE-SEC-002, the "Security Hardening" epic:
/// "Implement the view-audit-log read endpoint"). Until now the append-only audit log was WRITE-ONLY over
/// HTTP — every security-relevant command appended a record (CORE-AUD-001), and the read-side authorization
/// (<see cref="AuditQueryPolicy"/>) and safe projection (<see cref="AuditLogEntryView"/>) existed and were
/// unit-tested, but no route was mapped (csv/api_routes.csv defined none), so the dedicated Auditor role
/// could not fulfil its sole purpose. This story adds that route ON TOP of the existing policy + view,
/// without a parallel audit engine.
///
/// Route owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/audit-logs</c> — module <b>Audit</b>, roles "Owner,Admin,Auditor", a tenant-scoped,
///   paged read of the append-only audit log. The route carries no organization in its path, so the target
///   tenant is the required <c>?organizationSlug=</c> query parameter (exactly like the session start/end
///   commands and the reconnect-replay route), paged by the optional <c>?limit=</c> and <c>?offset=</c>.</item>
/// </list>
///
/// AUTHORIZATION (server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md "View audit log"; threats
/// T1/T5):
/// <list type="bullet">
///   <item>The trusted tenant is resolved by <see cref="TenantContextResolver"/> (token organization claim
///   AND persisted membership), so a service-account principal, a foreign/unclaimed tenant and a non-member
///   are all DENIED and hidden as 404 — a tenant the caller cannot see is indistinguishable from a missing
///   one (the "foreign-tenant 404/empty" acceptance criterion).</item>
///   <item>The caller is then a known member, so an insufficient role is a 403: only the secure-default
///   authorized set {Owner, Admin, Auditor} may read the log (<see cref="AuditQueryPolicy.CanViewAuditLog"/>,
///   an EXACT non-linear set membership; Host's matrix-<c>optional</c> grant fails closed and is denied).
///   This is the "non-privileged role 403" acceptance criterion.</item>
/// </list>
///
/// TENANT-SCOPED AND PAGED. The entries are read ONLY through
/// <see cref="IAuditLogRepository.ListPageByOrganizationAsync"/> for the resolved tenant, so one tenant's
/// records are never returned through another's id (threat T5). The unbounded log is never returned whole:
/// the page size is the optional <c>limit</c> (default <see cref="AuditLogPageResponse.DefaultLimit"/>,
/// clamped to <see cref="AuditLogPageResponse.MaxLimit"/>) and the page start is the optional zero-based
/// <c>offset</c>. The endpoint reads one extra row to set <see cref="AuditLogPageResponse.HasMore"/> without
/// a second COUNT.
///
/// PII / SECRET-FREE (threat T7). The result is the <see cref="AuditQueryPolicy.Project"/> projection into
/// <see cref="AuditLogEntryView"/> — identifiers, enums and generic state names only, never a display name,
/// email, token, storage coordinate or resolved content — so the read can never leak content the audit log
/// itself does not store. Calling <see cref="AuditQueryPolicy.Project"/> (not a hand-rolled mapping) keeps
/// the safe-projection AND the deny-by-default backstop both in one place.
///
/// FAIL-CLOSED ORDERING. Mirroring the reconnect-replay route, the optional paging parameters are validated
/// only AFTER authorization, so an unauthorized caller never receives request-shape feedback: a denied
/// tenant is 404 and an insufficient role is 403 regardless of a malformed <c>limit</c>/<c>offset</c>.
///
/// Persistence dependency: like the organization/session endpoints, this uses the tenant context resolver
/// and the audit log repository, registered only when a database connection string is configured (see
/// <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class AuditLogEndpoints
{
    /// <summary>Required query parameter naming the target organization (the tenant whose audit log is read).</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    /// <summary>Optional query parameter carrying the page size (defaults/clamps to the response bounds).</summary>
    private const string _limitQuery = "limit";

    /// <summary>Optional query parameter carrying the zero-based page offset.</summary>
    private const string _offsetQuery = "offset";

    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token
        // is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/audit-logs")
            .RequireAuthorization();

        group.MapGet("/", ReadAuditLogAsync);

        return endpoints;
    }

    // GET /api/v1/audit-logs?organizationSlug={slug}&limit={n}&offset={n}
    private static async Task<IResult> ReadAuditLogAsync(
        HttpContext httpContext,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        [FromQuery(Name = _limitQuery)] string? limit,
        [FromQuery(Name = _offsetQuery)] string? offset,
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
        // organization, so it is a query parameter exactly like the session start/end commands and the
        // reconnect-replay route.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // Resolve the trusted tenant context (token claim AND persisted membership). A denied resolution —
        // a service-account principal, a foreign/unknown/unclaimed tenant, or a non-member — is hidden as
        // 404, so a tenant the caller cannot see is indistinguishable from a missing one (threat T5;
        // "foreign-tenant 404"). The resolver canonicalizes the slug and never throws on a malformed one.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenAuditLog();
        }

        var context = resolution.Context;

        // "View audit log" is Owner/Admin/Auditor only (docs/06_AUTHORIZATION_MATRIX.md). The caller is a
        // known member of the tenant (the resolution proved it), so an insufficient role is a 403. The grant
        // is the exact, non-linear set-membership check the audit policy owns (never a >/< ladder); Host's
        // matrix-optional grant fails closed and is denied.
        if (!AuditQueryPolicy.CanViewAuditLog(context.Role))
        {
            return Forbidden();
        }

        // Authorized. Only now validate the optional paging parameters, so an unauthorized caller never
        // receives request-shape feedback (mirrors the reveal/replay commands). A present-but-malformed value
        // is a 400; an absent value uses the default page.
        if (!TryResolveLimit(limit, out var pageLimit, out var limitError))
        {
            return ValidationError(limitError);
        }

        if (!TryResolveOffset(offset, out var pageOffset, out var offsetError))
        {
            return ValidationError(offsetError);
        }

        // Read the tenant-scoped page. Fetch ONE extra row so HasMore can be set without a second COUNT
        // query; if it comes back, trim it off and report that a further page exists.
        var rows = await deps.AuditLog
            .ListPageByOrganizationAsync(context.OrganizationId, pageOffset, pageLimit + 1, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > pageLimit;
        var pageEntries = hasMore ? rows.Take(pageLimit) : rows;

        // Project through the audit policy (not a hand-rolled mapping): it yields the safe, PII/secret-free
        // AuditLogEntryView list for the authorized role and an EMPTY list for any unauthorized role, so the
        // deny-by-default backstop and the safe projection live in one place (threat T7).
        var entries = AuditQueryPolicy.Project(pageEntries, context.Role);

        var response = new AuditLogPageResponse(
            context.OrganizationId,
            pageOffset,
            pageLimit,
            hasMore,
            entries);

        return Results.Ok(response);
    }

    /// <summary>
    /// Resolves the requested page size: an absent value uses <see cref="AuditLogPageResponse.DefaultLimit"/>;
    /// a present value must be a positive integer (a non-integer or a value below one is a 400, mirroring the
    /// replay cursor's reject-malformed rule) and is clamped down to <see cref="AuditLogPageResponse.MaxLimit"/>
    /// so a single read can never materialize an unbounded number of rows.
    /// </summary>
    private static bool TryResolveLimit(string? raw, out int limit, out string error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            limit = AuditLogPageResponse.DefaultLimit;
            error = string.Empty;
            return true;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < 1)
        {
            limit = 0;
            error = $"The '{_limitQuery}' value must be a positive integer.";
            return false;
        }

        limit = Math.Min(parsed, AuditLogPageResponse.MaxLimit);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Resolves the requested zero-based page offset: an absent value is the first page (zero); a present
    /// value must be a non-negative integer (a non-integer or a negative value is a 400).
    /// </summary>
    private static bool TryResolveOffset(string? raw, out int offset, out string error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            offset = 0;
            error = string.Empty;
            return true;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < 0)
        {
            offset = 0;
            error = $"The '{_offsetQuery}' value must be a non-negative integer.";
            return false;
        }

        offset = parsed;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out AuditLogEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var auditLog = services.GetService<IAuditLogRepository>();

        if (resolver is null || auditLog is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new AuditLogEndpointDependencies(resolver, auditLog);
        return true;
    }

    /// <summary>
    /// Maps the authenticated principal to an <see cref="OidcPrincipal"/>, fail-closed: a failed mapping
    /// yields <see langword="false"/> (the caller returns 401). The mapping error reason is never echoed
    /// (threat T7).
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
            detail: "Reading the audit log requires persistence, which is not configured.");

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

    private static IResult Forbidden()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status403Forbidden,
            code: ProblemCodes.PermissionDenied,
            title: "Forbidden",
            detail: "You are not authorized to perform this action.");

    // Audit-log access is hidden: a service account, a foreign/unclaimed/unknown tenant and a non-member are
    // ALL reported as 404, never distinguishable and never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenAuditLog()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct AuditLogEndpointDependencies(
        TenantContextResolver Resolver,
        IAuditLogRepository AuditLog);
}
