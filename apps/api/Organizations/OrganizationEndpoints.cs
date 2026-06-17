// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.IdentityAccess;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Organizations;

/// <summary>
/// HTTP endpoints of the Organizations module (CORE-API-001: organization
/// create and read API). The Organizations module already owned the
/// <see cref="Organization"/> aggregate, its repositories and the tenant
/// context resolver, but no endpoint was mapped; this story surfaces the two
/// routes csv/api_routes.csv already defines, realizing the documented request
/// flow "authentication middleware -> tenant context -> endpoint ->
/// authorization" (docs/02_ARCHITECTURE.md) and following the
/// <c>WorkspaceEndpoints.cs</c> pattern.
///
/// Routes owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET  /api/v1/organizations</c> — list the organizations the
///   caller belongs to ("Only organizations user belongs to"). The result is
///   the intersection of the caller's persisted memberships and the token's
///   organization claims, so it never lists a tenant the caller is not a
///   member of, nor one the token does not assert.</item>
///   <item><c>POST /api/v1/organizations</c> — create a new tenant and make the
///   caller its founding <c>Owner</c> ("Creates organization and Owner
///   membership"), atomically.</item>
///   <item><c>DELETE /api/v1/organizations/{organizationSlug}</c>
///   — delete an organization, tearing the whole tenant down (CORE-PRIV-002,
///   tenant offboarding / data deletion), "Delete organization" (Owner ONLY —
///   stricter than member management). Runs the
///   <see cref="OrganizationDeletionService"/>: the tenant root is hard-deleted and
///   the schema's <c>ON DELETE CASCADE</c> foreign keys remove its workspaces,
///   sessions, participants, memberships and its OWN audit log (the audit log is
///   intentionally part of the teardown); the offboarding is appended to the audit
///   log as a PLATFORM-LEVEL fact (<see cref="AuditAction.OrganizationDeleted"/>)
///   that survives the teardown. A non-Owner tenant member is denied 403.
///   Fail-closed and hidden as 404 for a cross-tenant/unknown organization.</item>
///   <item><c>DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}</c>
///   — remove an organization member (CORE-LIFE-001), "Manage members" (Owner or
///   Admin). Hard-deletes the membership, revoking the subject's tenant access on
///   their next request; the sole tenant Owner cannot be removed (the last-Owner
///   invariant, 409 — an ownerless tenant would be permanently unreachable); the
///   removal is appended to the append-only audit log
///   (<see cref="AuditAction.MemberRemoved"/>). Fail-closed and hidden as 404 for a
///   cross-tenant/unknown organization or member.</item>
///   <item><c>DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data</c>
///   — erase a data subject's personal data (CORE-PRIV-001, GDPR Art.17 "right to
///   erasure"), Owner or Admin. Resolves the member to their global user profile and
///   runs the <see cref="DataSubjectErasureService"/>: the subject's profile (OIDC
///   subject, email, display name) is deleted, their participant display data and
///   invited-email rows are anonymized, and their exports/assets survive anonymized
///   (and memberships are revoked) via the schema's <c>ON DELETE SET NULL</c>/
///   <c>CASCADE</c> foreign keys; the erasure is appended to the PII-free audit log
///   by id (<see cref="AuditAction.UserProfileErased"/>). The sole Owner of an
///   organization cannot be erased (the orphan invariant, 409). Fail-closed and
///   hidden as 404 for a cross-tenant/unknown organization or member.</item>
///   <item><c>GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export</c>
///   — obtain a machine-readable access/portability export of a data subject's
///   personal data (CORE-PRIV-004, GDPR Art.15 access / Art.20 portability), the
///   access counterpart of the erasure above and DISTINCT from the session Exports
///   feature. Resolves the member to their global user profile and runs the
///   <see cref="PersonalDataExportService"/>: the export gathers the documented
///   personal-data set scoped to the resolved tenant (the subject's identity
///   profile plus their organization membership, workspace memberships, participant
///   records and the invitations addressed to their email) and the access is
///   audited by id (<see cref="AuditAction.PersonalDataExported"/>). The PII is
///   delivered ONLY to the entitled recipient: the data subject THEMSELVES (self-service)
///   or an Owner/Admin acting on their behalf; any other tenant member is denied
///   403. Fail-closed and hidden as 404 for a cross-tenant/unknown organization or
///   member.</item>
/// </list>
///
/// Authorization model (server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md;
/// threats T1/T5):
/// <list type="bullet">
///   <item>The authenticated principal is mapped fail-closed from the request's
///   claims to an <see cref="OidcPrincipal"/>; a failed mapping is 401.</item>
///   <item>Both routes are user-tenant operations: only a human user holds an
///   organization membership (an <see cref="OrganizationMember"/> references a
///   user profile), so a service-account principal is denied 403 — exactly as
///   the tenant context resolver denies a non-user (<c>NotAUser</c>) and the
///   <c>/me</c> routes deny a service account. This is the unauthorized-role
///   denial for these routes.</item>
///   <item>The tenant boundary is the token's organization claim, matched
///   exactly (<see cref="OidcPrincipal.HasOrganizationClaim"/>) — the same
///   token-asserted boundary <see cref="TenantContextResolver"/> enforces. On
///   create, a slug the token does not claim is a FOREIGN tenant and is hidden
///   as 404 (fail-closed; the create never reveals whether that tenant exists).
///   On list, an organization the token does not claim is simply not returned.
///   </item>
///   <item>Creating a tenant that already exists (a taken slug) is a 409 and
///   adds NO membership, so a caller can never escalate a create into ownership
///   of a pre-existing organization (threats T5/T1).</item>
/// </list>
///
/// The tenant context resolver is not invoked by the create or list routes: it
/// resolves an EXISTING organization the caller is already a member of, which
/// neither the create (the tenant does not exist yet) nor the cross-tenant list
/// (many organizations, not one) is. Instead those two routes reuse the
/// resolver's building blocks — the same OIDC principal mapping, the same exact
/// organization-claim check, the same repositories and membership model — so the
/// per-organization decision is identical to what the resolver would grant. The
/// member removal route, by contrast, acts on one EXISTING organization the
/// caller belongs to, so it DOES use <see cref="TenantContextResolver"/>
/// directly (token claim AND persisted membership), exactly like the
/// workspace-scoped routes.
///
/// Persistence dependency: the endpoints use the organization repository and the
/// user-profile reference service, which are registered only when a database
/// connection string is configured (see <c>Program.cs</c>). When persistence is
/// off the endpoints fail closed with 503, keeping the smoke tests green.
/// </summary>
internal static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so
        // a missing/invalid token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/organizations")
            .RequireAuthorization();

        group.MapGet("/", ListOrganizationsAsync);
        group.MapGet("/{organizationSlug}/members/{memberId}/personal-data-export", ExportMemberPersonalDataAsync);
        group.MapPost("/", CreateOrganizationAsync);
        group.MapDelete("/{organizationSlug}", DeleteOrganizationAsync);
        group.MapDelete("/{organizationSlug}/members/{memberId}", RemoveOrganizationMemberAsync);
        group.MapDelete("/{organizationSlug}/members/{memberId}/personal-data", EraseMemberPersonalDataAsync);

        return endpoints;
    }

    // GET /api/v1/organizations
    private static async Task<IResult> ListOrganizationsAsync(
        HttpContext httpContext,
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

        // "Organizations the user belongs to" is inherently a user concept: a
        // service account holds no user profile and no organization membership,
        // so it is denied fail-closed (403), exactly as the tenant resolver
        // denies a non-user (NotAUser) and the /me routes deny a service account.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Resolve the current user's profile (the canonical "current user"
        // resolution; idempotent on first sight), then list the organizations the
        // subject is a member of.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var organizations = await deps.Organizations
            .ListByMemberAsync(profile.Id, cancellationToken)
            .ConfigureAwait(false);

        // Defence in depth (threat T5): return only an organization the token
        // ALSO asserts as a claim, so the listing can never diverge from what the
        // tenant resolver would grant per organization (claim AND membership). A
        // persisted membership without a matching token claim — a foreign tenant
        // from the token's point of view — is never listed.
        var response = organizations
            .Where(organization => principal.HasOrganizationClaim(organization.Slug))
            .Select(OrganizationResponse.From)
            .ToArray();

        return Results.Ok(response);
    }

    // POST /api/v1/organizations
    private static async Task<IResult> CreateOrganizationAsync(
        HttpContext httpContext,
        [FromBody] CreateOrganizationRequest? request,
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

        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        // Validate the inputs before any authorization/tenant work, so a broken
        // body is a 400 regardless of who is calling (mirrors WorkspaceEndpoints).
        if (!Organization.IsValidName(request.Name?.Trim()))
        {
            return ValidationError("A valid organization name is required.");
        }

        string canonicalSlug;
        try
        {
            canonicalSlug = Organization.CanonicalizeSlug(request.Slug);
        }
        catch (ArgumentException)
        {
            return ValidationError("A valid organization slug is required.");
        }

        // Only a human user can own a tenant: the founding membership is a user
        // membership (OrganizationMember references a user profile). A service
        // account is denied fail-closed (403), exactly as the tenant resolver
        // denies a non-user and the /me routes deny a service account.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Tenant boundary (threat T5): the token must ASSERT this tenant. A caller
        // can only create the organization their identity provider scoped the
        // token to; creating a slug the token does not claim is a FOREIGN tenant
        // and is hidden as 404 (fail-closed; the create never reveals whether the
        // tenant already exists). This is the same exact organization-claim check
        // the tenant context resolver performs.
        if (!principal.HasOrganizationClaim(canonicalSlug))
        {
            return HiddenOrganization();
        }

        // Provision the caller's user profile (idempotent on first sight; the
        // canonical "current user" resolution), so the founding owner membership
        // can reference it.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var organization = Organization.Create(canonicalSlug, request.Name!.Trim(), now);
        var owner = OrganizationMember.Create(organization.Id, profile.Id, MembershipRole.Owner, now);

        // Create the tenant and its founding Owner membership ATOMICALLY: a tenant
        // is never left without an owner, and a duplicate slug rolls the
        // membership back too (no privilege escalation into a pre-existing tenant).
        var addResult = await deps.Organizations
            .AddWithOwnerAsync(organization, owner, cancellationToken)
            .ConfigureAwait(false);
        if (addResult == OrganizationAddResult.DuplicateSlug)
        {
            // The slug is the globally-unique natural key, so a second create is a
            // 409 Conflict (docs/08_API_CONTRACTS.md). The error carries no other
            // tenant data, and no membership was created.
            return CoreProblem.Create(
                statusCode: StatusCodes.Status409Conflict,
                code: ProblemCodes.DuplicateResource,
                title: "Conflict",
                detail: "An organization with this slug already exists.");
        }

        var response = OrganizationResponse.From(organization);
        return Results.Created($"/api/v1/organizations/{organization.Id}", response);
    }

    // DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}
    private static async Task<IResult> RemoveOrganizationMemberAsync(
        HttpContext httpContext,
        string organizationSlug,
        string memberId,
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

        // The organization is identified by its slug in the path (the tenant's natural key, matched against
        // the token's organization claim). A missing/blank slug or a malformed member id can never address a
        // stored row; hide as 404, never echoing why.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenOrganization();
        }

        if (!Guid.TryParse(memberId, out var memberGuid) || memberGuid == Guid.Empty)
        {
            return HiddenOrganization();
        }

        // Resolve the trusted tenant context (token claim AND persisted membership). A denied resolution —
        // a foreign/unknown tenant, a malformed slug, a non-member or a service-account principal — is
        // hidden as 404, so a tenant the caller cannot see is indistinguishable from a missing one (threat
        // T5). The resolver canonicalizes the slug and never throws on a malformed one.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenOrganization();
        }

        var context = resolution.Context;

        // "Manage members" is Owner/Admin only (docs/06_AUTHORIZATION_MATRIX.md). The caller is a known
        // member of the tenant (the resolution proved it), so an insufficient role is a 403. Exact,
        // non-linear role check (no >/<).
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Find the target membership WITHIN this tenant; a member id that belongs to another organization
        // (or no membership at all) is hidden as 404, never 403, so a member outside the caller's resolved
        // tenant can never be probed for (threats T1/T5).
        var target = await deps.OrganizationMembers
            .FindByIdAsync(context.OrganizationId, memberGuid, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return HiddenOrganization();
        }

        // Last-Owner invariant: the sole Owner of the tenant cannot be removed — an ownerless organization
        // would be permanently unreachable, since the tenant resolver requires a membership. Only relevant
        // when the target itself is an Owner; the count is tenant-scoped. This is an invariant conflict
        // (409), not an authorization failure.
        if (target.HasRole(MembershipRole.Owner))
        {
            var ownerCount = await deps.OrganizationMembers
                .CountByRoleAsync(context.OrganizationId, MembershipRole.Owner, cancellationToken)
                .ConfigureAwait(false);
            if (ownerCount <= 1)
            {
                return LastOwnerConflict();
            }
        }

        // Capture the removed identity/role BEFORE deletion for the audit record.
        var removedMemberId = target.Id;
        var removedRole = target.Role;

        await deps.OrganizationMembers.RemoveAsync(target, cancellationToken).ConfigureAwait(false);

        // AUDIT: removal is security-relevant access revocation, so append an append-only audit record
        // capturing the actor (the admin who removed the member), the removed membership and the revoked
        // role (threats T1/T6). An organization member removal is organization-level, so it records no
        // workspace. The audit row outlives the now-deleted membership (recorded fact, not FK).
        var now = timeProvider.GetUtcNow();
        var entry = AuditLogEntry.ForMemberRemoval(
            context.OrganizationId,
            workspaceId: null,
            context.UserProfileId,
            nameof(OrganizationMember),
            removedMemberId,
            removedRole.ToString(),
            now);
        await deps.AuditLog.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        // The membership row IS the access grant, so its deletion has already revoked the subject's tenant
        // access; nothing is returned (204 No Content).
        return Results.NoContent();
    }

    // DELETE /api/v1/organizations/{organizationSlug}
    private static async Task<IResult> DeleteOrganizationAsync(
        HttpContext httpContext,
        string organizationSlug,
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

        // The organization is identified by its slug in the path (the tenant's natural key, matched against the
        // token's organization claim). A missing/blank slug can never address a stored tenant; hide as 404, never
        // echoing why.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenOrganization();
        }

        // Resolve the trusted tenant context (token claim AND persisted membership). A denied resolution — a
        // foreign/unknown tenant, a malformed slug, a non-member or a service-account principal — is hidden as
        // 404, so a tenant the caller cannot see is indistinguishable from a missing one (threat T5), exactly like
        // the member-removal route.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenOrganization();
        }

        var context = resolution.Context;

        // Deleting a tenant is the most destructive tenant action, so it is OWNER-ONLY
        // (docs/06_AUTHORIZATION_MATRIX.md "Delete organization") — stricter than member management (Owner/Admin).
        // The caller is a known member of the tenant (the resolution proved it), so an insufficient role — an
        // Admin, or any other tenant role — is a 403. Exact, non-linear role check (no >/<).
        if (!context.HasRole(MembershipRole.Owner))
        {
            return Forbidden();
        }

        var now = timeProvider.GetUtcNow();
        var result = await deps.OrganizationDeletion
            .DeleteAsync(context.OrganizationId, context.UserProfileId, now, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            // The tenant is deleted (the cascade removed its workspaces, sessions, participants, memberships and
            // its own audit log) and the platform-level offboarding fact survives; nothing is returned (204).
            OrganizationDeletionResult.Deleted => Results.NoContent(),

            // Defensive: the resolution proved the tenant exists, so this should not occur; if it was deleted
            // concurrently, hide it as 404 rather than reveal anything (fail-closed).
            _ => HiddenOrganization(),
        };
    }

    // DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data
    private static async Task<IResult> EraseMemberPersonalDataAsync(
        HttpContext httpContext,
        string organizationSlug,
        string memberId,
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

        // The organization is identified by its slug in the path (the tenant's natural key, matched against the
        // token's organization claim). A missing/blank slug or a malformed member id can never address a stored
        // row; hide as 404, never echoing why.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenOrganization();
        }

        if (!Guid.TryParse(memberId, out var memberGuid) || memberGuid == Guid.Empty)
        {
            return HiddenOrganization();
        }

        // Resolve the trusted tenant context (token claim AND persisted membership). A denied resolution — a
        // foreign/unknown tenant, a malformed slug, a non-member or a service-account principal — is hidden as
        // 404, so a tenant the caller cannot see is indistinguishable from a missing one (threat T5), exactly
        // like the member-removal route.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenOrganization();
        }

        var context = resolution.Context;

        // The right to erasure is a member-management privilege: Owner/Admin only (docs/06_AUTHORIZATION_MATRIX.md),
        // exactly as the member-removal route. The caller is a known member of the tenant (the resolution proved
        // it), so an insufficient role is a 403. Exact, non-linear role check (no >/<).
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Identify the data subject by their membership in THIS tenant; a member id that belongs to another
        // organization (or no membership at all) is hidden as 404, never 403, so a member outside the caller's
        // resolved tenant can never be probed for (threats T1/T5). The membership resolves the subject's global
        // user-profile id, which the erasure command then acts on.
        var target = await deps.OrganizationMembers
            .FindByIdAsync(context.OrganizationId, memberGuid, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return HiddenOrganization();
        }

        var now = timeProvider.GetUtcNow();
        var result = await deps.DataSubjectErasure
            .EraseAsync(context.OrganizationId, context.UserProfileId, target.UserProfileId, now, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            // The subject's personal data is erased (profile deleted, participant/invitation PII anonymized,
            // exports/assets anonymized and memberships revoked via the schema foreign keys) and audited by id;
            // nothing is returned (204 No Content).
            DataSubjectErasureResult.Erased => Results.NoContent(),

            // The caller is authorized; the orphan invariant, not the caller, is the reason, so this is a 409
            // Conflict (docs/08_API_CONTRACTS.md).
            DataSubjectErasureResult.SubjectIsSoleOrganizationOwner => SoleOwnerErasureConflict(),

            // Defensive: the membership references a real user profile by foreign key, so the subject should
            // always exist; if it somehow does not, hide it as 404 rather than reveal anything (fail-closed).
            _ => HiddenOrganization(),
        };
    }

    // GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export
    private static async Task<IResult> ExportMemberPersonalDataAsync(
        HttpContext httpContext,
        string organizationSlug,
        string memberId,
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

        // The organization is identified by its slug in the path (the tenant's natural key, matched against the
        // token's organization claim). A missing/blank slug or a malformed member id can never address a stored
        // row; hide as 404, never echoing why.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenOrganization();
        }

        if (!Guid.TryParse(memberId, out var memberGuid) || memberGuid == Guid.Empty)
        {
            return HiddenOrganization();
        }

        // Resolve the trusted tenant context (token claim AND persisted membership). A denied resolution — a
        // foreign/unknown tenant, a malformed slug, a non-member or a service-account principal — is hidden as
        // 404, so a tenant the caller cannot see is indistinguishable from a missing one (threat T5), exactly like
        // the member-removal and erasure routes.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenOrganization();
        }

        var context = resolution.Context;

        // Identify the data subject by their membership in THIS tenant; a member id that belongs to another
        // organization (or no membership at all) is hidden as 404, never 403, so a member outside the caller's
        // resolved tenant can never be probed for (threats T1/T5). The membership resolves the subject's global
        // user-profile id, which the export command then assembles the personal data for.
        var target = await deps.OrganizationMembers
            .FindByIdAsync(context.OrganizationId, memberGuid, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return HiddenOrganization();
        }

        // AUTHORIZATION (the story's "PII delivered only to the entitled subject" criterion;
        // docs/06_AUTHORIZATION_MATRIX.md). The export discloses a subject's personal data, so it is delivered ONLY
        // to those entitled to it: the data subject THEMSELVES (self-service access), or an Owner/Admin acting on
        // their behalf (the tenant's data controller, which GDPR permits). The caller is already a known member of
        // the tenant (the resolution proved it), so any OTHER tenant member — a Host/CoHost/Participant/Observer/
        // Auditor who is not the subject — is denied 403, fail-closed. Exact, non-linear role check (no >/<); the
        // self check matches the caller's resolved user profile against the target member's subject.
        var callerIsSubject = context.UserProfileId == target.UserProfileId;
        var callerIsTenantAdmin = context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin);
        if (!(callerIsSubject || callerIsTenantAdmin))
        {
            return Forbidden();
        }

        var now = timeProvider.GetUtcNow();
        var export = await deps.PersonalDataExport
            .ExportAsync(context.OrganizationId, context.UserProfileId, target.UserProfileId, now, cancellationToken)
            .ConfigureAwait(false);

        // Defensive: the membership references a real user profile by foreign key, so the subject should always
        // exist; if it somehow does not, hide it as 404 rather than reveal anything (fail-closed).
        return export is null
            ? HiddenOrganization()
            : Results.Ok(PersonalDataExportResponse.From(export));
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They
    /// exist only when a database connection string is configured; when absent,
    /// the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out OrganizationEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var organizations = services.GetService<IOrganizationRepository>();
        var userProfiles = services.GetService<UserProfileReferenceService>();
        var resolver = services.GetService<TenantContextResolver>();
        var organizationMembers = services.GetService<IOrganizationMemberRepository>();
        var auditLog = services.GetService<IAuditLogRepository>();
        var dataSubjectErasure = services.GetService<DataSubjectErasureService>();
        var organizationDeletion = services.GetService<OrganizationDeletionService>();
        var personalDataExport = services.GetService<PersonalDataExportService>();

        if (organizations is null
            || userProfiles is null
            || resolver is null
            || organizationMembers is null
            || auditLog is null
            || dataSubjectErasure is null
            || organizationDeletion is null
            || personalDataExport is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new OrganizationEndpointDependencies(
            organizations,
            userProfiles,
            resolver,
            organizationMembers,
            auditLog,
            dataSubjectErasure,
            organizationDeletion,
            personalDataExport);
        return true;
    }

    /// <summary>
    /// Maps the authenticated principal to an <see cref="OidcPrincipal"/>,
    /// fail-closed: a failed mapping yields <see langword="false"/> (the caller
    /// returns 401). The mapping error reason is never echoed (threat T7).
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
            detail: "Organization operations require persistence, which is not configured.");

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

    // The last Owner of an organization cannot be removed: an ownerless tenant
    // would be permanently unreachable (the tenant resolver requires a
    // membership). The caller is authorized; the invariant, not the caller, is
    // the reason, so this is a 409 Conflict (docs/08_API_CONTRACTS.md). The
    // detail names only the generic invariant and leaks no tenant data (threat T7).
    private static IResult LastOwnerConflict()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.Conflict,
            title: "Conflict",
            detail: "The last Owner of the organization cannot be removed.");

    // A data subject who is the sole Owner of an organization cannot be erased: erasing them cascade-removes
    // their memberships, leaving that tenant permanently unreachable. The caller is authorized; the orphan
    // invariant, not the caller, is the reason, so this is a 409 Conflict (docs/08_API_CONTRACTS.md). The detail
    // names only the generic invariant and leaks no tenant data (threat T7).
    private static IResult SoleOwnerErasureConflict()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.Conflict,
            title: "Conflict",
            detail: "The sole Owner of an organization cannot be erased; transfer ownership first.");

    // Tenant existence is hidden: a foreign tenant (a slug the token does not
    // claim) is reported as 404, never echoing the reason (docs/08; threat T5).
    private static IResult HiddenOrganization()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct OrganizationEndpointDependencies(
        IOrganizationRepository Organizations,
        UserProfileReferenceService UserProfiles,
        TenantContextResolver Resolver,
        IOrganizationMemberRepository OrganizationMembers,
        IAuditLogRepository AuditLog,
        DataSubjectErasureService DataSubjectErasure,
        OrganizationDeletionService OrganizationDeletion,
        PersonalDataExportService PersonalDataExport);
}
