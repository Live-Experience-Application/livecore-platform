// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Organizations;

/// <summary>
/// Resolved, trusted tenant context of the Organizations module (CORE-ID-005).
///
/// A <see cref="TenantContext"/> is the single output of the tenant context
/// resolver: it answers "which organization is this authenticated caller acting
/// in, and is the caller actually entitled to it" with a value that downstream
/// authorization policies can trust without re-checking the tenant boundary
/// (docs/02_ARCHITECTURE.md request flow: the tenant context resolver runs
/// before the authorization policy; docs/05_MODULE_CONTRACTS.md: the
/// Organizations module "Provides: organization context, tenant isolation
/// checks"; docs/06_AUTHORIZATION_MATRIX.md: "Organization boundary must be
/// checked before workspace boundary").
///
/// A context only ever exists in an entitled state: it is produced solely by
/// <see cref="TenantContextResolver"/>, which constructs it only after the
/// organization claim, the organization, the subject's user profile and the
/// subject's membership in exactly that organization have all been verified.
/// There is no public constructor and no way to assemble a context from a
/// foreign organization's data, so holding a <see cref="TenantContext"/> for an
/// organization is itself proof of entitlement to that one organization (threat
/// T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// Tenant boundary: the context names exactly one organization
/// (<see cref="OrganizationId"/> / <see cref="OrganizationSlug"/>), one subject
/// (<see cref="UserProfileId"/>) and the subject's generic role in that
/// organization (<see cref="Role"/>). The role is carried as data only. The
/// roles are not linearly ordered, so this type exposes an exact-role check
/// (<see cref="HasRole"/>) and never a "greater than" ladder; mapping a role to
/// per-action capabilities is a later story (the workspace authorization policy
/// stories) and is deliberately not done here (docs/06_AUTHORIZATION_MATRIX.md;
/// MembershipRole xmldoc).
/// </summary>
public sealed class TenantContext
{
    private TenantContext(
        Guid organizationId,
        string organizationSlug,
        Guid userProfileId,
        MembershipRole role)
    {
        OrganizationId = organizationId;
        OrganizationSlug = organizationSlug;
        UserProfileId = userProfileId;
        Role = role;
    }

    /// <summary>
    /// Surrogate id of the organization the caller is acting in (the
    /// <c>organization_id</c> every tenant-scoped table later filters on,
    /// docs/10_DATABASE_SCHEMA.md). This is the tenant boundary downstream
    /// queries must scope to.
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Canonical slug of the same organization, carried for logging and for
    /// matching the token's organization claim. Equal to the organization claim
    /// value that entitled the caller.
    /// </summary>
    public string OrganizationSlug { get; }

    /// <summary>
    /// Surrogate id of the caller's user profile reference (the <c>user_id</c>
    /// of the subject inside this tenant, CORE-ID-002). Identifies the subject
    /// for downstream object-level authorization.
    /// </summary>
    public Guid UserProfileId { get; }

    /// <summary>
    /// The subject's generic role in <see cref="OrganizationId"/>, taken from
    /// the persisted membership (CORE-ID-004). Authorization data only; not
    /// interpreted into per-action capabilities here.
    /// </summary>
    public MembershipRole Role { get; }

    /// <summary>
    /// Whether the resolved role is exactly the given role. Roles are not
    /// linearly ordered, so this is an exact match, never a "at least" ladder
    /// (MembershipRole xmldoc; docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    public bool HasRole(MembershipRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
        }

        return Role == role;
    }

    /// <summary>
    /// Builds the trusted context once entitlement has been verified. Internal
    /// so only <see cref="TenantContextResolver"/> can mint a context, and only
    /// from a verified organization, subject and membership: the membership's
    /// own organization must match the resolved organization, and the
    /// membership's subject must match the resolved subject, so a context can
    /// never carry a foreign organization's id or a foreign subject's role
    /// (threat T5).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// The organization or membership is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The membership does not belong to exactly the given organization and
    /// subject. This is an internal invariant guard: the resolver only ever
    /// passes a membership it already loaded for this (organization, subject)
    /// pair, so a mismatch indicates a programming error and fails closed.
    /// </exception>
    internal static TenantContext Create(
        Organization organization,
        Guid userProfileId,
        OrganizationMember membership)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(membership);

        if (!membership.Identifies(organization.Id, userProfileId))
        {
            throw new ArgumentException(
                "The membership does not belong to the resolved organization and subject; a tenant context is never minted from foreign data.",
                nameof(membership));
        }

        return new TenantContext(
            organization.Id,
            organization.Slug,
            userProfileId,
            membership.Role);
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs:
    /// organization id and slug, subject id and role. All four are
    /// identifiers/authorization metadata, never free-form content (threat T7
    /// in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"TenantContext org={OrganizationId} slug={OrganizationSlug} sub={UserProfileId} role={Role}";
}
