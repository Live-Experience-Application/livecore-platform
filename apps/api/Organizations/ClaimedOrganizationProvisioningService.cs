// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.Organizations;

/// <summary>
/// First-login tenant provisioning service of the Organizations module
/// (CORE-ID-007). It provisions the organization a caller's VERIFIED OIDC token
/// organization claim names but that does NOT yet exist, founding the caller as
/// its <see cref="MembershipRole.Owner"/>, so org-scoped reads resolve on the
/// first <c>GET /api/v1/me</c> without an out-of-band
/// <c>POST /api/v1/organizations</c>. It is the founding-owner counterpart of the
/// <see cref="TenantContextResolver"/>: the resolver turns a principal + an
/// EXISTING tenant into a trusted context, this turns a principal + a
/// not-yet-existing CLAIMED tenant into one.
///
/// The Organizations module owns the <c>organizations</c> and
/// <c>organization_members</c> tables (docs/05_MODULE_CONTRACTS.md), so the tenant
/// and its founding owner are created ONLY through the module's own atomic
/// founding-owner path <see cref="IOrganizationRepository.AddWithOwnerAsync"/> —
/// the SAME path <c>POST /api/v1/organizations</c> uses — and the IdentityAccess
/// <c>/me</c> endpoint that drives this never writes those tables directly.
///
/// Security model (fail-closed; threats T5/T1 in
/// docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>A claim is provisioned ONLY when it is itself a valid canonical
///   <see cref="Organization.Slug"/>: the provisioned tenant's slug must EQUAL
///   the claim value, because both the resolver and the <c>/me</c> intersection
///   match the token claim against the canonical slug EXACTLY (ordinal). A claim
///   that is not a canonical slug names no resolvable tenant and is skipped, so a
///   tenant the founder could never reach is never created.</item>
///   <item>Provision-on-first-sight is idempotent: a claim naming an
///   ALREADY-EXISTING tenant provisions NOTHING — the founder is never
///   auto-enrolled in a pre-existing organization, so an existing tenant can
///   never be joined or hijacked from a claim. Such a caller resolves through the
///   normal claim-AND-persisted-membership gate and is hidden if not already a
///   member.</item>
///   <item>The create race is fail-closed: if a concurrent first-login created
///   the same tenant between the existence check and the atomic create, the
///   unique slug index rejects the insert (<see cref="OrganizationAddResult.DuplicateSlug"/>)
///   and the founding membership rolls back WITH it, so the caller is never
///   enrolled in the tenant another caller founded.</item>
///   <item>Only a human user founds a tenant: a service-account principal holds
///   no user profile or membership, so nothing is provisioned for it.</item>
/// </list>
/// </summary>
public sealed class ClaimedOrganizationProvisioningService
{
    private readonly IOrganizationRepository _organizations;
    private readonly TimeProvider _timeProvider;

    public ClaimedOrganizationProvisioningService(
        IOrganizationRepository organizations,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _organizations = organizations;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Provisions any organization the principal's verified token organization
    /// claims name but that does not yet exist, founding
    /// <paramref name="founderUserProfileId"/> as the <see cref="MembershipRole.Owner"/>
    /// of each, idempotent on the unique slug index. Returns the number of tenants
    /// actually provisioned (0 when the principal asserts no provisionable claim,
    /// every claimed tenant already exists, or the principal is not a user).
    /// </summary>
    /// <param name="principal">
    /// The authenticated caller. Only the verified token organization claims and
    /// the user/service-account distinction are consumed; the caller's profile is
    /// resolved by the caller and passed as <paramref name="founderUserProfileId"/>.
    /// </param>
    /// <param name="founderUserProfileId">
    /// The caller's already-resolved user-profile id, which the founding owner
    /// membership references (never a client-supplied id).
    /// </param>
    /// <param name="cancellationToken">Cancels the lookups and the atomic create.</param>
    /// <exception cref="ArgumentNullException">The principal is null.</exception>
    /// <exception cref="ArgumentException">The founder id is empty.</exception>
    public async Task<int> ProvisionClaimedTenantsAsync(
        OidcPrincipal principal,
        Guid founderUserProfileId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (founderUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Founder (user profile) id must not be empty.", nameof(founderUserProfileId));
        }

        // Only a human user founds a tenant. A service account is a machine client (CORE-ID-002) with no user
        // profile or organization membership, so it provisions nothing — defence in depth, since the /me endpoint
        // already denies a service account before reaching here.
        if (principal.Type != PrincipalType.User)
        {
            return 0;
        }

        var provisioned = 0;
        var now = _timeProvider.GetUtcNow();
        foreach (var claim in principal.OrganizationClaims)
        {
            // The provisioned tenant's canonical slug must EQUAL the claim value, because the resolver and the /me
            // intersection match the token claim against the canonical slug exactly (ordinal). A claim that is not
            // itself a valid canonical slug (wrong casing, whitespace, too short/long, illegal characters) names no
            // tenant the founder could ever resolve, so it is skipped rather than coerced into an unreachable org.
            if (!Organization.IsValidSlug(claim))
            {
                continue;
            }

            // Provision-on-first-sight, idempotent: if a tenant with this slug already exists, provision NOTHING.
            // The founder is never auto-enrolled in a pre-existing tenant (threats T5/T1); they resolve through the
            // normal claim-AND-membership gate and are hidden if not already a member. The positive lookup is
            // served from the authorization cache (CachingOrganizationRepository); a missing tenant is never cached,
            // so a brand-new slug is always re-checked against the database.
            var existing = await _organizations.FindBySlugAsync(claim, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                continue;
            }

            // Found the tenant and its Owner membership ATOMICALLY through the Organizations-owned founding-owner
            // path — the SAME atomic create POST /api/v1/organizations uses. The display name defaults to the slug
            // (a valid name): there is no request body on /me, and the slug is a stable, product-neutral label the
            // Owner can later rename.
            var organization = Organization.Create(claim, claim, now);
            var owner = OrganizationMember.Create(organization.Id, founderUserProfileId, MembershipRole.Owner, now);
            var result = await _organizations
                .AddWithOwnerAsync(organization, owner, cancellationToken)
                .ConfigureAwait(false);
            if (result == OrganizationAddResult.Added)
            {
                provisioned++;
            }

            // OrganizationAddResult.DuplicateSlug: a concurrent first-login founded this same tenant between the
            // existence check above and this atomic create. The unique slug index rejected the insert and the
            // founding membership rolled back with it, so the caller is NOT enrolled in the tenant the other caller
            // founded — fail-closed, provision nothing. The caller still resolves the now-existing tenant only if
            // they are genuinely a member (they are not, unless they founded it), exactly like the existing-tenant
            // branch above.
        }

        return provisioned;
    }
}
