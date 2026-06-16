using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Templates;

/// <summary>
/// HTTP endpoints of the Templates module's organization-scoped TEMPLATE routes. A <see cref="Template"/> is
/// reusable vertical scaffolding stored entirely as DATA (its template key plus the entity types it defines)
/// through which a vertical maps its domain onto Core (the template boundary, docs/04_PRODUCT_BOUNDARIES.md).
/// The Templates module already owned the <see cref="Template"/> registry aggregate, its scope-aware repository,
/// the entity-type loader (CORE-ENT-004) and a single DELETE route (CORE-LIFE-008); this surface adds the
/// authoring create/list/read so an Owner/Admin can define, list and read its tenant's templates over the API
/// (CORE-TMPL-001, the "Vertical Authoring and Read API Completeness" epic). They realize the documented request
/// flow end-to-end — "authentication middleware -> tenant context resolver -> endpoint -> authorization policy"
/// (docs/02_ARCHITECTURE.md) — mirroring the organization member-removal route's org-scoped shape.
///
/// Routes owned here, all under <c>/api/v1/organizations/{organizationSlug}/templates</c> (csv/api_routes.csv),
/// all roles Owner/Admin:
/// <list type="bullet">
///   <item><c>POST /api/v1/organizations/{organizationSlug}/templates</c> — creates an ORGANIZATION-scoped
///   template via <see cref="TemplateCreationService"/>: the template is created with a SERVER-MINTED surrogate
///   id and the creation is appended to the append-only audit log (<see cref="Audit.AuditAction.TemplateCreated"/>)
///   — the insert and the audit committing together in one transaction. Returns <c>201 Created</c> with the
///   <see cref="TemplateResponse"/>. A key+version the organization already holds is a <c>409 Conflict</c>.</item>
///   <item><c>GET  /api/v1/organizations/{organizationSlug}/templates</c> — lists the organization's templates in
///   the deterministic (key then version) order via <see cref="ITemplateRepository.ListByOrganizationAsync"/>,
///   projected to <see cref="TemplateResponse"/>. ORG-SCOPED ONLY: a GLOBAL template is never included.</item>
///   <item><c>GET  /api/v1/organizations/{organizationSlug}/templates/{templateId}</c> — reads ONE template
///   within the resolved tenant via <see cref="ITemplateRepository.FindByOrganizationAndIdAsync"/>. A
///   foreign/unknown template — or a GLOBAL template addressed by id — is a hidden <c>404</c>.</item>
///   <item><c>DELETE /api/v1/organizations/{organizationSlug}/templates/{templateId}</c> — the pre-existing
///   delete (CORE-LIFE-008), unchanged.</item>
/// </list>
///
/// THE GLOBAL/ORGANIZATION TEMPLATE BOUNDARY (the story's headline requirement; csv/database_tables.csv
/// row 19 "global/organization" scope; threat T5 in docs/07_SECURITY_THREAT_MODEL.md): a GLOBAL template
/// (<c>organization_id IS NULL</c>) is available to every tenant and is platform/seed data, never authored,
/// listed or mutated through this tenant surface; an ORGANIZATION-scoped template is owned by, and visible only
/// within, its one tenant. This is enforced structurally rather than by a branch: the create ALWAYS calls
/// <see cref="Template.CreateForOrganization"/> (never the global factory), and the list and read use the
/// org-scoped repository methods (<see cref="ITemplateRepository.ListByOrganizationAsync"/> /
/// <see cref="ITemplateRepository.FindByOrganizationAndIdAsync"/>), which match only rows whose
/// <c>organization_id</c> equals the resolved tenant — a global template is NEVER returned through the org path,
/// so "an org-scoped lookup never returns a global template for mutation" holds at every route, and a global
/// template addressed by its exact id is an indistinguishable hidden 404. <see cref="TemplateResponse.From"/>
/// rejects a global template defensively, keeping the guarantee even at the projection layer.
///
/// Authorization model (server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5), mirroring the
/// organization member-removal route because a template is an organization-level admin resource, not workspace
/// content. A template is an authoring/registry artifact, not audience content, so — like the entity-type routes
/// — ALL routes are restricted to the privileged roles (the story: "An Owner/Admin can create, list and read …
/// non-privileged roles cannot create"), with no host-vs-participant projection because no audience role ever
/// receives a template:
/// <list type="bullet">
///   <item>The authenticated principal is mapped fail-closed from the request's claims to an
///   <see cref="OidcPrincipal"/>; a failed mapping is 401.</item>
///   <item>For the create the body is validated for SHAPE first (a valid dotted-slug template key, a positive
///   version, a well-formed/structurally-valid definition) — a broken body is a 400 regardless of authorization,
///   and the rejected content is never echoed back (threat T7).</item>
///   <item>A missing/blank <c>organizationSlug</c> or a malformed <c>templateId</c> can never address a stored
///   row, so each is hidden as 404 (never echoing why).</item>
///   <item>The trusted tenant context is resolved by <see cref="TenantContextResolver"/> (token claim AND
///   persisted membership). A denied resolution — a foreign/unknown tenant, a non-member or a service-account
///   principal — is hidden as 404, so a tenant the caller cannot see is indistinguishable from a missing one
///   (threat T5).</item>
///   <item>Managing an organization template is the "authorized admin" action, so the role set is
///   <c>Owner</c>/<c>Admin</c> (the same admin set the delete and organization member-removal routes use). The
///   caller is a known member of the tenant (the resolution proved it), so an insufficient role is a 403.
///   <see cref="MembershipRole"/> is non-linear, so the check is EXACT set membership, never an ordering
///   comparison.</item>
///   <item>Once authorized, a template id that addresses no organization template in the resolved tenant — an
///   unknown id, a template owned by another organization, or a GLOBAL template — is a SAFE hidden 404.</item>
/// </list>
///
/// Persistence dependency: like the organization endpoints, these use the tenant context resolver, the template
/// repository and the template create service, which are registered only when a database connection string is
/// configured (see <c>Program.cs</c>). When persistence is off the endpoint fails closed with 503 rather than
/// crashing startup.
/// </summary>
internal static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/organizations")
            .RequireAuthorization();

        group.MapGet("/{organizationSlug}/templates", ListTemplatesAsync);
        group.MapPost("/{organizationSlug}/templates", CreateTemplateAsync);
        group.MapGet("/{organizationSlug}/templates/{templateId}", GetTemplateByIdAsync);
        group.MapDelete("/{organizationSlug}/templates/{templateId}", DeleteTemplateAsync);

        return endpoints;
    }

    // GET /api/v1/organizations/{organizationSlug}/templates
    private static async Task<IResult> ListTemplatesAsync(
        HttpContext httpContext,
        string organizationSlug,
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
            return HiddenTemplate();
        }

        var authorization = await TryAuthorizeOwnerOrAdminAsync(deps, principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Result is not null)
        {
            return authorization.Result;
        }

        var context = authorization.Context!;

        // The templates are returned in the deterministic (template key then version) order the repository
        // enforces. ListByOrganizationAsync is ORG-SCOPED ONLY, so a global template is never included (threat
        // T5).
        var templates = await deps.Templates
            .ListByOrganizationAsync(context.OrganizationId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(templates.Select(TemplateResponse.From).ToArray());
    }

    // GET /api/v1/organizations/{organizationSlug}/templates/{templateId}
    private static async Task<IResult> GetTemplateByIdAsync(
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

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenTemplate();
        }

        // A malformed template id can never address a stored row; treat it as hidden (404).
        if (!Guid.TryParse(templateId, out var templateGuid) || templateGuid == Guid.Empty)
        {
            return HiddenTemplate();
        }

        var authorization = await TryAuthorizeOwnerOrAdminAsync(deps, principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Result is not null)
        {
            return authorization.Result;
        }

        var context = authorization.Context!;

        // Load the template WITHIN the resolved tenant. FindByOrganizationAndIdAsync matches only a row whose
        // organization_id equals the resolved tenant, so a template owned by another organization OR a GLOBAL
        // template (organization_id IS NULL) is never returned even when the surrogate id is known — exactly how
        // "an org-scoped lookup never returns a global template" is enforced (threats T1/T5). An unknown id is a
        // safe hidden 404.
        var template = await deps.Templates
            .FindByOrganizationAndIdAsync(context.OrganizationId, templateGuid, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return HiddenTemplate();
        }

        return Results.Ok(TemplateResponse.From(template));
    }

    // POST /api/v1/organizations/{organizationSlug}/templates
    private static async Task<IResult> CreateTemplateAsync(
        HttpContext httpContext,
        string organizationSlug,
        [FromBody] CreateTemplateRequest? request,
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

        // Validate the template inputs before touching the tenant, so a broken body is a 400 regardless of
        // authorization. The key, version and definition are template-/host-supplied DATA validated for SHAPE
        // only (the template boundary, docs/04); the rejected content is never echoed back (threat T7). The key
        // is canonicalized (trim + lower-case) and checked against the canonical dotted-slug shape; the version
        // defaults to the minimum (1) when omitted, so the common single-version create needs no version field.
        var templateKey = request.TemplateKey?.Trim().ToLowerInvariant();
        if (!Template.IsValidTemplateKey(templateKey))
        {
            return ValidationError("A valid template key is required.");
        }

        var version = request.Version ?? Template.MinVersion;
        if (version < Template.MinVersion)
        {
            return ValidationError("A valid template version is required.");
        }

        var definition = request.Definition?.Trim();
        if (!TemplateDefinitionValidator.IsValidDefinition(definition))
        {
            return ValidationError("A valid template definition is required.");
        }

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenTemplate();
        }

        var authorization = await TryAuthorizeOwnerOrAdminAsync(deps, principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Result is not null)
        {
            return authorization.Result;
        }

        var context = authorization.Context!;

        // Authorized: create the ORGANIZATION-scoped template (persisting it and appending the TemplateCreated
        // audit record) atomically. The service resolves any existing key+version within the organization first,
        // so a key+version the tenant already holds is a 409 Conflict that creates nothing; the per-scope unique
        // index remains the race guard. It ALWAYS creates an organization-scoped template — never a global one.
        var now = timeProvider.GetUtcNow();
        var result = await deps.TemplateCreation
            .CreateAsync(
                context.OrganizationId,
                templateKey!,
                version,
                definition!,
                context.UserProfileId,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == TemplateCreationStatus.Duplicate)
        {
            return DuplicateTemplate();
        }

        var template = result.Template!;
        var response = TemplateResponse.From(template);
        return Results.Created(
            $"/api/v1/organizations/{organizationSlug}/templates/{template.Id}", response);
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

        // The organization is identified by its slug in the path (the tenant's natural key, matched against the
        // token's organization claim by the resolver). A missing/blank slug or a malformed template id can never
        // address a stored row; hide as 404, never echoing why.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenTemplate();
        }

        if (!Guid.TryParse(templateId, out var templateGuid) || templateGuid == Guid.Empty)
        {
            return HiddenTemplate();
        }

        // Resolve the trusted tenant context (token claim AND persisted membership). A denied resolution —
        // a foreign/unknown tenant, a malformed slug, a non-member or a service-account principal — is hidden as
        // 404, so a tenant the caller cannot see is indistinguishable from a missing one (threat T5). The
        // resolver canonicalizes the slug and never throws on a malformed one.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenTemplate();
        }

        var context = resolution.Context;

        // Deleting an organization template is the "authorized admin" action, so it is Owner/Admin only — the
        // same admin set the organization member-removal route uses. The caller is a known member of the tenant
        // (the resolution proved it), so an insufficient role is a 403. Exact, non-linear role check (no >/<).
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Load the template WITHIN the resolved tenant. FindByOrganizationAndIdAsync matches only a row whose
        // organization_id equals the resolved tenant, so a template owned by another organization OR a GLOBAL
        // template (organization_id IS NULL) is never returned even when the surrogate id is known — this is
        // exactly how the "global templates cannot be deleted by an org" boundary is enforced (threat T5/T1). An
        // unknown id is simply null. Deleting a template the caller cannot address is a SAFE hidden 404: it
        // reveals nothing and changes nothing.
        var template = await deps.Templates
            .FindByOrganizationAndIdAsync(context.OrganizationId, templateGuid, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return HiddenTemplate();
        }

        // Hard-delete exactly this one organization template. Only the templates row is removed; the workspace
        // EntityType rows a previous load materialized carry no foreign key back to the template, so
        // already-instantiated entity types are unaffected (the story acceptance criterion).
        await deps.Templates.RemoveAsync(template, cancellationToken).ConfigureAwait(false);

        // The registry row is gone; nothing is returned (204 No Content).
        return Results.NoContent();
    }

    /// <summary>
    /// Resolves the trusted tenant context for the route's organization (token claim AND persisted membership)
    /// and authorizes the caller as an Owner/Admin of that tenant — the shared authorization for the create,
    /// list and read routes (the delete route inlines the identical steps). A denied resolution (a
    /// foreign/unknown tenant, a non-member or a service-account principal) is hidden as 404, so a tenant the
    /// caller cannot see is indistinguishable from a missing one (threat T5); a known member who lacks the
    /// Owner/Admin role is 403. <see cref="MembershipRole"/> is non-linear, so this is an EXACT set-membership
    /// check, never an ordering comparison. Returns a non-null <see cref="TemplateAuthorizationOutcome.Result"/>
    /// (the error response) when the caller is denied, or a null result with the resolved
    /// <see cref="TemplateAuthorizationOutcome.Context"/> when the caller is an authorized admin.
    /// </summary>
    private static async Task<TemplateAuthorizationOutcome> TryAuthorizeOwnerOrAdminAsync(
        TemplateEndpointDependencies deps,
        OidcPrincipal principal,
        string organizationSlug,
        CancellationToken cancellationToken)
    {
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return new TemplateAuthorizationOutcome(HiddenTemplate(), null);
        }

        var context = resolution.Context;

        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return new TemplateAuthorizationOutcome(Forbidden(), context);
        }

        return new TemplateAuthorizationOutcome(null, context);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out TemplateEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var templates = services.GetService<ITemplateRepository>();
        var templateCreation = services.GetService<TemplateCreationService>();

        if (resolver is null || templates is null || templateCreation is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new TemplateEndpointDependencies(resolver, templates, templateCreation);
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

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: detail);

    // The organization already holds a template with this key and version (the per-scope unique natural key). The
    // caller is authorized; the existing template, not the caller, is the reason, so this is a 409 Conflict. The
    // detail names only the generic condition and leaks no tenant data (threat T7). The same key+version stays
    // available in another organization or in the global pool (threat T5).
    private static IResult DuplicateTemplate()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "A template with this key and version already exists in the organization.");

    // Template existence is hidden: a malformed slug/template id, a foreign or non-entitled tenant, a template
    // owned by another organization, a GLOBAL template (never exposed through the org route) and an unknown
    // template are ALL reported as 404, never distinguishable from each other and never echoing the reason
    // (docs/08; threats T1/T5).
    private static IResult HiddenTemplate()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    /// <summary>
    /// The result of the shared authorization step: a non-null <see cref="Result"/> is the error response to
    /// return (a hidden 404 for a denied tenant resolution, a 403 for a member without Owner/Admin); a null
    /// result carries the resolved tenant <see cref="Context"/> and means the caller is an authorized admin and
    /// the handler may proceed.
    /// </summary>
    private readonly record struct TemplateAuthorizationOutcome(IResult? Result, TenantContext? Context);

    private readonly record struct TemplateEndpointDependencies(
        TenantContextResolver Resolver,
        ITemplateRepository Templates,
        TemplateCreationService TemplateCreation);
}
