using LiveCore.Api.Organizations;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Response projection of the authenticated caller's principal context
/// (CORE-API-002, <c>GET /api/v1/me</c>, csv/api_routes.csv: "Returns principal
/// context only"). It is the safe, generic answer to "who am I, and what can I
/// act as?": the caller's own user profile plus the organization memberships the
/// caller holds and the role in each.
///
/// SECURITY (threat T7 in docs/07_SECURITY_THREAT_MODEL.md, and the story note
/// "No secrets/tokens in the response"): the projection carries identifiers and
/// the caller's own display metadata only — never the access token, the raw
/// organization claim values, an authorization rationale or any other secret. The
/// membership list is the INTERSECTION of the caller's persisted memberships and
/// the token's organization claims (computed by the endpoint), so the principal
/// context never exposes a tenant the token does not assert (threat T5).
/// </summary>
/// <param name="User">The caller's own user profile.</param>
/// <param name="Memberships">
/// The caller's organization memberships (organization + role), filtered to the
/// tenants the token claims and ordered by the organizations' time-ordered id.
/// </param>
public sealed record CurrentPrincipalResponse(
    CurrentUserResponse User,
    IReadOnlyList<OrganizationMembershipResponse> Memberships)
{
    /// <summary>
    /// Projects the resolved <paramref name="profile"/> and the already
    /// token-filtered <paramref name="memberships"/> into the principal-context
    /// response.
    /// </summary>
    public static CurrentPrincipalResponse From(
        UserProfile profile,
        IReadOnlyList<OrganizationMembershipResponse> memberships)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(memberships);

        return new CurrentPrincipalResponse(CurrentUserResponse.From(profile), memberships);
    }
}

/// <summary>
/// Response projection of the caller's own user profile (CORE-API-002). It is the
/// caller's own identity returned to themselves: the surrogate profile id, the
/// OIDC identity pair (issuer + subject) that keys the profile, and the optional
/// display metadata mirrored from the provider. These are identifiers and the
/// caller's own metadata — not secrets — but they are still the caller's OWN data
/// only; this DTO is never used to project another user (threat T7).
/// </summary>
/// <param name="Id">Surrogate id of the caller's user profile (UUIDv7).</param>
/// <param name="Issuer">Identity of the OIDC provider that asserted the caller.</param>
/// <param name="Subject">Issuer-local stable identifier of the caller.</param>
/// <param name="DisplayName">Optional human-readable name mirrored from the provider.</param>
/// <param name="Email">Optional email address mirrored from the provider.</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string Issuer,
    string Subject,
    string? DisplayName,
    string? Email)
{
    /// <summary>
    /// Projects a <see cref="UserProfile"/> into its principal-context DTO. Only
    /// the generic identity and display fields are copied; no credential or token
    /// is stored on the profile, so none can leak here.
    /// </summary>
    public static CurrentUserResponse From(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new CurrentUserResponse(
            profile.Id,
            profile.Issuer,
            profile.SubjectId,
            profile.DisplayName,
            profile.Email);
    }
}

/// <summary>
/// Response projection of one of the caller's organization memberships
/// (CORE-API-002): the organization the caller belongs to and the generic role
/// the caller holds in it. The role is the stable generic
/// <see cref="MembershipRole"/> name (e.g. <c>Owner</c>), product-neutral per
/// docs/03_DOMAIN_LANGUAGE.md — a vertical maps it to its own label in its UI. The
/// projection carries the tenant's public identity (id, slug, name) and the
/// caller's role only; it includes no internal authorization rationale (threat
/// T7).
/// </summary>
/// <param name="OrganizationId">Surrogate id of the organization (UUIDv7).</param>
/// <param name="OrganizationSlug">Globally-unique natural key of the organization.</param>
/// <param name="OrganizationName">Human-readable display name of the organization.</param>
/// <param name="Role">The generic role the caller holds in the organization.</param>
public sealed record OrganizationMembershipResponse(
    Guid OrganizationId,
    string OrganizationSlug,
    string OrganizationName,
    string Role)
{
    /// <summary>
    /// Projects an <see cref="OrganizationMembershipView"/> (organization + role)
    /// into its response DTO, serializing the role as its stable generic name.
    /// </summary>
    public static OrganizationMembershipResponse From(OrganizationMembershipView membership)
    {
        ArgumentNullException.ThrowIfNull(membership);

        return new OrganizationMembershipResponse(
            membership.Organization.Id,
            membership.Organization.Slug,
            membership.Organization.Name,
            membership.Role.ToString());
    }
}
