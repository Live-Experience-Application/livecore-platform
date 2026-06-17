// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Organizations;

/// <summary>
/// Request body for creating an organization (CORE-API-001,
/// <c>POST /api/v1/organizations</c>, csv/api_routes.csv: "Creates organization
/// and Owner membership").
///
/// An organization is the Core-owned, product-neutral tenant root
/// (docs/03_DOMAIN_LANGUAGE.md), so the create names the NEW tenant directly by
/// its own <see cref="Slug"/> and display <see cref="Name"/>; unlike a workspace
/// create it carries no parent-organization field, because the organization IS
/// the boundary being created. The caller may only create the tenant their token
/// is scoped to: the slug is matched against the principal's organization claims
/// before anything is persisted (the same token-asserted boundary the tenant
/// context resolver enforces; threat T5 in docs/07_SECURITY_THREAT_MODEL.md), and
/// the creator is recorded as the founding <c>Owner</c>.
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md): a
/// slug (the globally-unique natural key) and a display name only.
/// </summary>
/// <param name="Slug">
/// Globally-unique natural key of the new organization (lower-case, URL-safe).
/// </param>
/// <param name="Name">Human-readable display name of the organization.</param>
public sealed record CreateOrganizationRequest(string? Slug, string? Name);

/// <summary>
/// Response projection of an organization (CORE-API-001). Generic and
/// product-neutral (docs/04_PRODUCT_BOUNDARIES.md, docs/08_API_CONTRACTS.md):
/// the surrogate id, the natural key, the display name and server timestamps
/// only. It carries no membership/role detail (the caller's principal context —
/// memberships and roles — is the separate <c>GET /api/v1/me</c> projection,
/// CORE-API-002) and no internal authorization rationale (docs/08 DTO design
/// rules; threat T7).
/// </summary>
/// <param name="Id">Surrogate id of the organization (UUIDv7), the tenant root.</param>
/// <param name="Slug">Globally-unique natural key of the organization.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="CreatedAt">When the organization was created (UTC).</param>
/// <param name="UpdatedAt">When the organization was last updated (UTC).</param>
public sealed record OrganizationResponse(
    Guid Id,
    string Slug,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects an <see cref="Organization"/> aggregate into its response DTO.
    /// Only the generic, non-sensitive fields are copied.
    /// </summary>
    public static OrganizationResponse From(Organization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);

        return new OrganizationResponse(
            organization.Id,
            organization.Slug,
            organization.Name,
            organization.CreatedAt,
            organization.UpdatedAt);
    }
}
