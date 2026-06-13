using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Observability;

namespace LiveCore.Api.Organizations;

/// <summary>
/// Tenant context resolver of the Organizations module (CORE-ID-005).
///
/// This is the single chokepoint that turns an authenticated principal plus a
/// target organization into a trusted <see cref="TenantContext"/> or a
/// fail-closed denial. It sits in the request flow between authentication and
/// authorization (docs/02_ARCHITECTURE.md: "tenant/workspace context resolver
/// -> endpoint -> authorization policy") and realizes the Organizations
/// module's contract to provide "organization context" and "tenant isolation
/// checks" (docs/05_MODULE_CONTRACTS.md). The authorization principle it
/// enforces is "Organization boundary must be checked before workspace
/// boundary" (docs/06_AUTHORIZATION_MATRIX.md).
///
/// Resolution rule (defence in depth, threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md): a user principal is entitled to a target
/// organization only when ALL of the following hold, checked in order and
/// denying at the first failure:
/// <list type="number">
///   <item>the principal is a human user (service accounts have no user
///   membership, CORE-ID-002);</item>
///   <item>the principal's organization claims include the target slug — the
///   token-asserted tenant boundary (CORE-ID-001's <c>OrganizationClaims</c>,
///   matched against the canonical <c>Organization.Slug</c> per the Organization
///   xmldoc), checked before any database access;</item>
///   <item>an organization exists for the target slug;</item>
///   <item>the principal's OIDC identity has a persisted user profile reference
///   (CORE-ID-002);</item>
///   <item>that subject has a membership in exactly the target organization
///   (CORE-ID-004).</item>
/// </list>
/// Both the organization CLAIM and the persisted MEMBERSHIP must match. The
/// claim is the provider's assertion that the caller's token is scoped to the
/// tenant; the membership is the platform's own authoritative grant. Requiring
/// both means neither a forged/stale membership without a matching token claim,
/// nor a broad token claim without an actual membership, can cross the tenant
/// boundary. The resolved role comes only from the persisted membership, never
/// from the token.
///
/// The resolver takes explicit inputs (an already-mapped
/// <see cref="OidcPrincipal"/> and a target slug) rather than reaching into an
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> or HttpContext, so it
/// stays a plain, unit-testable service. Wiring it to the authentication
/// middleware / HttpContext and exposing it through endpoints is a follow-up in
/// this epic; this story builds the resolver only.
/// </summary>
public sealed class TenantContextResolver
{
    private readonly IOrganizationRepository _organizations;
    private readonly IUserProfileRepository _userProfiles;
    private readonly IOrganizationMemberRepository _members;

    // The per-request structured log context (CORE-OBS-002), enriched with the resolved organization_id on a
    // successful resolution. Optional so the resolver stays a plain, unit-testable service: DI injects the
    // request-scoped instance the log-scope middleware opened in the running host, and it is left null in
    // tests that exercise the decision logic directly.
    private readonly RequestLogContext? _requestLogContext;

    public TenantContextResolver(
        IOrganizationRepository organizations,
        IUserProfileRepository userProfiles,
        IOrganizationMemberRepository members,
        RequestLogContext? requestLogContext = null)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(userProfiles);
        ArgumentNullException.ThrowIfNull(members);

        _organizations = organizations;
        _userProfiles = userProfiles;
        _members = members;
        _requestLogContext = requestLogContext;
    }

    /// <summary>
    /// Resolves the trusted tenant context for the given user principal acting
    /// in the organization identified by <paramref name="targetOrganizationSlug"/>,
    /// or a typed fail-closed denial. The slug may be passed in any casing or
    /// with surrounding whitespace; it is canonicalized before use. An
    /// unparseable (non-slug-shaped) target can never address a tenant and is
    /// reported as <see cref="TenantContextResolutionError.OrganizationNotFound"/>
    /// rather than throwing, so a malformed request from an authenticated caller
    /// is denied, not crashed.
    /// </summary>
    /// <exception cref="ArgumentNullException">The principal is null.</exception>
    public async Task<TenantContextResolutionResult> ResolveAsync(
        OidcPrincipal principal,
        string targetOrganizationSlug,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Only human users hold a user profile and an organization membership.
        // Service accounts are machine clients (CORE-ID-002); they are not
        // resolved into a user tenant context here.
        if (principal.Type != PrincipalType.User)
        {
            return TenantContextResolutionResult.Denied(TenantContextResolutionError.NotAUser);
        }

        // Canonicalize the target slug. A value that is not even a valid slug
        // shape cannot name any tenant: deny as "not found" instead of throwing
        // so a hostile/broken target is a clean denial.
        string canonicalSlug;
        try
        {
            canonicalSlug = Organization.CanonicalizeSlug(targetOrganizationSlug);
        }
        catch (ArgumentException)
        {
            return TenantContextResolutionResult.Denied(TenantContextResolutionError.OrganizationNotFound);
        }

        // Defence in depth: the token must already assert this tenant. The
        // organization claim is matched exactly (ordinal, case-sensitive)
        // against the canonical slug (OidcPrincipal.HasOrganizationClaim;
        // Organization xmldoc). Check this before any database access so a
        // caller whose token is not scoped to the tenant is denied without
        // touching the tenant's data (threat T5).
        if (!principal.HasOrganizationClaim(canonicalSlug))
        {
            return TenantContextResolutionResult.Denied(TenantContextResolutionError.ClaimMismatch);
        }

        // The target must resolve to a real organization. The repository
        // performs an exact slug lookup; a missing tenant denies.
        var organization = await _organizations
            .FindBySlugAsync(canonicalSlug, cancellationToken)
            .ConfigureAwait(false);
        if (organization is null)
        {
            return TenantContextResolutionResult.Denied(TenantContextResolutionError.OrganizationNotFound);
        }

        // The subject must be known to the platform: a user profile keyed by
        // the full OIDC identity pair (issuer, subject) must exist (CORE-ID-002).
        // The lookup is by the full pair only, so a foreign subject is never
        // matched (threat T5).
        var userProfile = await _userProfiles
            .FindByOidcIdentityAsync(principal.Issuer, principal.SubjectId, cancellationToken)
            .ConfigureAwait(false);
        if (userProfile is null)
        {
            return TenantContextResolutionResult.Denied(TenantContextResolutionError.UnknownSubject);
        }

        // The authoritative grant: the subject must have a membership in
        // exactly the target organization. The lookup is tenant-scoped on the
        // (organization_id, user_id) pair, so a membership the subject holds in
        // another organization is never returned (threat T5; CORE-ID-004).
        var membership = await _members
            .FindAsync(organization.Id, userProfile.Id, cancellationToken)
            .ConfigureAwait(false);
        if (membership is null)
        {
            return TenantContextResolutionResult.Denied(TenantContextResolutionError.NoMembership);
        }

        // Every check passed: mint the trusted context. TenantContext.Create
        // re-asserts that the membership belongs to exactly this organization
        // and subject, so the context can never carry foreign data.
        var context = TenantContext.Create(organization, userProfile.Id, membership);

        // Enrich the per-request log context with the resolved organization id (CORE-OBS-002). Only an
        // ENTITLED resolution reaches here, so the logged organization_id is always one the caller is a member
        // of — a denied/foreign tenant is never enriched (it is a fail-closed denial above) and never logged.
        // The id is a surrogate identifier, never content (threat T7).
        _requestLogContext?.SetOrganizationId(context.OrganizationId);

        return TenantContextResolutionResult.Success(context);
    }
}
