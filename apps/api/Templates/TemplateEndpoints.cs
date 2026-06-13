using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.Templates;

/// <summary>
/// HTTP endpoint of the Templates module's template DELETION (CORE-LIFE-008, the "Resource
/// Lifecycle and Deletion" epic). The Templates module already owned the <see cref="Template"/>
/// registry aggregate, its scope-aware repository and the entity-type loader (CORE-ENT-004), but a
/// template could be CREATED and LOADED, never removed. This adds the missing inverse so an
/// authorized admin can delete an organization-scoped template.
///
/// Route owned by this story:
/// <list type="bullet">
///   <item><c>DELETE /api/v1/organizations/{organizationSlug}/templates/{templateId}</c> — module
///   Templates, roles Owner/Admin. Hard-deletes ONE organization-scoped template, addressed within
///   its owning tenant. Returns <c>204 No Content</c> on success.</item>
/// </list>
///
/// THE GLOBAL/ORGANIZATION TEMPLATE BOUNDARY (the story's headline requirement; csv/database_tables.csv
/// row 19 "global/organization" scope; threat T5 in docs/07_SECURITY_THREAT_MODEL.md): a GLOBAL
/// template (<c>organization_id IS NULL</c>) is available to every tenant and must NOT be deletable by
/// an organization, while an ORGANIZATION-scoped template is owned by, and deletable only within, its
/// one tenant. This is enforced structurally rather than by a branch: the template is loaded through
/// <see cref="ITemplateRepository.FindByOrganizationAndIdAsync"/>, which matches only a row whose
/// <c>organization_id</c> equals the resolved tenant — a global template is NEVER returned through the
/// org path, so an organization's attempt to delete a global template (even with its exact id) is an
/// indistinguishable hidden 404 and the global template is left untouched. The route is org-scoped (the
/// tenant's slug is in the path, like the organization member-removal route), so there is no global
/// delete path at all from this surface.
///
/// Already-instantiated entity types are unaffected (the story acceptance criterion): the loader
/// materializes NORMAL workspace <c>EntityType</c> rows that carry no foreign key back to the template,
/// so deleting the <c>templates</c> row removes only the registry entry and leaves every previously
/// loaded type in place. There is nothing to cascade.
///
/// Authorization model (server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5),
/// mirroring the organization member-removal route because a template is an organization-level admin
/// resource, not workspace content:
/// <list type="bullet">
///   <item>The authenticated principal is mapped fail-closed from the request's claims to an
///   <see cref="OidcPrincipal"/>; a failed mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> or a malformed <c>templateId</c> can never address a
///   stored row, so each is hidden as 404 (never echoing why).</item>
///   <item>The trusted tenant context is resolved by <see cref="TenantContextResolver"/> (token claim
///   AND persisted membership). A denied resolution — a foreign/unknown tenant, a non-member or a
///   service-account principal — is hidden as 404, so a tenant the caller cannot see is
///   indistinguishable from a missing one (threat T5).</item>
///   <item>Deleting an organization template is the "authorized admin" action, so the role set is
///   <c>Owner</c>/<c>Admin</c> (the same "Manage members"/"Manage workspace settings" admin set the
///   organization member-removal route uses). The caller is a known member of the tenant (the
///   resolution proved it), so an insufficient role is a 403. <see cref="MembershipRole"/> is
///   non-linear, so the check is EXACT set membership, never an ordering comparison.</item>
///   <item>Once authorized, a template id that addresses no organization template in the resolved
///   tenant — an unknown id, a template owned by another organization, or a GLOBAL template — is a
///   SAFE hidden 404: it reveals nothing and changes nothing (threats T1/T5).</item>
/// </list>
///
/// This story removes a registry row only; faithful to the CORE-ENT-004 template-create precedent
/// (which emitted no event and wrote no audit record) and the CORE-LIFE-002 entity-relationship-removal
/// precedent, it adds no event and no audit record — the threat model lists no audit control for the
/// template registry.
///
/// Persistence dependency: like the organization endpoints, this uses the tenant context resolver and
/// the template repository, which are registered only when a database connection string is configured
/// (see <c>Program.cs</c>). When persistence is off the endpoint fails closed with 503 rather than
/// crashing startup.
/// </summary>
internal static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid
        // token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/organizations")
            .RequireAuthorization();

        group.MapDelete("/{organizationSlug}/templates/{templateId}", DeleteTemplateAsync);

        return endpoints;
    }

    // DELETE /api/v1/organizations/{organizationSlug}/templates/{templateId}
    private static async Task<IResult> DeleteTemplateAsync(
        HttpContext httpContext,
        string organizationSlug,
        string templateId,
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

        // The organization is identified by its slug in the path (the tenant's natural key, matched
        // against the token's organization claim by the resolver). A missing/blank slug or a malformed
        // template id can never address a stored row; hide as 404, never echoing why.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenTemplate();
        }

        if (!Guid.TryParse(templateId, out var templateGuid) || templateGuid == Guid.Empty)
        {
            return HiddenTemplate();
        }

        // Resolve the trusted tenant context (token claim AND persisted membership). A denied
        // resolution — a foreign/unknown tenant, a malformed slug, a non-member or a service-account
        // principal — is hidden as 404, so a tenant the caller cannot see is indistinguishable from a
        // missing one (threat T5). The resolver canonicalizes the slug and never throws on a malformed one.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenTemplate();
        }

        var context = resolution.Context;

        // Deleting an organization template is the "authorized admin" action (the story acceptance
        // criterion), so it is Owner/Admin only — the same admin set the organization member-removal
        // route uses. The caller is a known member of the tenant (the resolution proved it), so an
        // insufficient role is a 403. Exact, non-linear role check (no >/<).
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Load the template WITHIN the resolved tenant. FindByOrganizationAndIdAsync matches only a row
        // whose organization_id equals the resolved tenant, so a template owned by another organization
        // OR a GLOBAL template (organization_id IS NULL) is never returned even when the surrogate id is
        // known — this is exactly how the "global templates cannot be deleted by an org" boundary is
        // enforced (threat T5/T1). An unknown id is simply null. Deleting a template the caller cannot
        // address is a SAFE hidden 404: it reveals nothing and changes nothing.
        var template = await deps.Templates
            .FindByOrganizationAndIdAsync(context.OrganizationId, templateGuid, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return HiddenTemplate();
        }

        // Hard-delete exactly this one organization template. Only the templates row is removed; the
        // workspace EntityType rows a previous load materialized carry no foreign key back to the
        // template, so already-instantiated entity types are unaffected (the story acceptance criterion).
        await deps.Templates.RemoveAsync(template, cancellationToken).ConfigureAwait(false);

        // The registry row is gone; nothing is returned (204 No Content).
        return Results.NoContent();
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a
    /// database connection string is configured; when absent, the endpoint fails closed with 503
    /// instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out TemplateEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var templates = services.GetService<ITemplateRepository>();

        if (resolver is null || templates is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new TemplateEndpointDependencies(resolver, templates);
        return true;
    }

    /// <summary>
    /// Maps the authenticated principal to an <see cref="OidcPrincipal"/>, fail-closed: a failed
    /// mapping yields <see langword="false"/> (the caller returns 401). The mapping error reason is
    /// never echoed (threat T7).
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
            detail: "Template operations require persistence, which is not configured.");

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

    // Template existence is hidden: a malformed slug/template id, a foreign or non-entitled tenant, a
    // template owned by another organization, a GLOBAL template (not deletable by an org) and an unknown
    // template are ALL reported as 404, never distinguishable from each other and never echoing the
    // reason (docs/08; threats T1/T5).
    private static IResult HiddenTemplate()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct TemplateEndpointDependencies(
        TenantContextResolver Resolver,
        ITemplateRepository Templates);
}
