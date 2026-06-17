// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Organizations;

/// <summary>
/// Organization membership aggregate of the Organizations module
/// (CORE-ID-004).
///
/// An <see cref="OrganizationMember"/> is the Core-owned, product-neutral link
/// that records that one subject (a user) belongs to one
/// <see cref="Organization"/> with one generic <see cref="MembershipRole"/>
/// (docs/05_MODULE_CONTRACTS.md: the Organizations module owns "organization
/// membership"; csv/database_tables.csv: table <c>organization_members</c>,
/// scope <c>organization</c>, "Tenant membership"). It is the first
/// authorization-relevant relationship in Core: later authorization policies
/// read this row to decide what a subject may do inside an organization.
///
/// Tenant boundary: the membership is tenant-scoped, so it carries
/// <see cref="OrganizationId"/> (docs/10_DATABASE_SCHEMA.md: tenant-scoped
/// tables include <c>organization_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). A membership grants standing only in the
/// organization it names: membership in organization A says nothing about
/// organization B. The subject is referenced by the surrogate id of its user
/// profile reference (<see cref="UserProfileId"/>, the <c>user_id</c> foreign
/// key to the <c>users</c> table, mirroring the
/// <c>workspace_members(workspace_id, user_id)</c> index in
/// docs/10_DATABASE_SCHEMA.md). No credentials are stored here: authentication
/// is owned by the external OIDC provider (ADR 0005).
///
/// Identity invariant: a subject has at most one membership per organization.
/// The pair (<see cref="OrganizationId"/>, <see cref="UserProfileId"/>) is the
/// natural key, immutable for the lifetime of the row, and enforced by a
/// unique database index. The surrogate <see cref="Id"/> is the row's own key.
///
/// Authorization invariant: <see cref="Role"/> is a generic role drawn from
/// the authorization matrix (docs/06_AUTHORIZATION_MATRIX.md). It is the only
/// authorization input on this aggregate; it is never inferred from the
/// display metadata of any other aggregate. The role can be changed
/// (<see cref="ChangeRole"/>) but the tenant boundary and the subject can not:
/// re-roling never moves a membership to another organization or another
/// subject.
/// </summary>
public sealed class OrganizationMember
{
    private OrganizationMember(
        Guid id,
        Guid organizationId,
        Guid userProfileId,
        MembershipRole role,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Membership id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
        }

        if (!IsValidRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A membership cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        UserProfileId = userProfileId;
        Role = role;
        // Timestamps are normalized to UTC so the persisted timestamptz
        // values are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private OrganizationMember()
    {
    }

    /// <summary>
    /// Surrogate key of the membership row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). The natural key stays the
    /// (organization, subject) pair.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the membership: the id of the organization this
    /// membership belongs to (the <c>organization_id</c> foreign key to the
    /// <c>organizations</c> table). Immutable; a membership never crosses to
    /// another organization (threat T5).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Subject of the membership: the surrogate id of the user profile
    /// reference (the <c>user_id</c> foreign key to the <c>users</c> table).
    /// Immutable; a membership never moves to another subject.
    /// </summary>
    public Guid UserProfileId { get; }

    /// <summary>
    /// Generic role the subject holds in this organization, drawn from the
    /// authorization matrix (docs/06_AUTHORIZATION_MATRIX.md). The only
    /// authorization input on this aggregate.
    /// </summary>
    public MembershipRole Role { get; private set; }

    /// <summary>When this membership was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this membership was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new membership linking the given subject to the given
    /// organization with the given generic role.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or subject id is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    public static OrganizationMember Create(
        Guid organizationId,
        Guid userProfileId,
        MembershipRole role,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            userProfileId,
            role,
            createdAt,
            createdAt);

    /// <summary>
    /// Whether this membership belongs to exactly the given organization.
    /// Empty ids match nothing. This is the tenant-boundary check: a
    /// membership grants standing only in the organization it names, never in
    /// any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this membership belongs to exactly the given subject. Empty ids
    /// match nothing.
    /// </summary>
    public bool BelongsToSubject(Guid userProfileId)
        => userProfileId != Guid.Empty && UserProfileId == userProfileId;

    /// <summary>
    /// Whether this membership is the one for exactly the given
    /// (organization, subject) pair. Both must match; an empty id matches
    /// nothing. A membership in another organization, even for the same
    /// subject, returns <see langword="false"/> (threat T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid userProfileId)
        => BelongsToOrganization(organizationId) && BelongsToSubject(userProfileId);

    /// <summary>
    /// Whether this membership holds exactly the given generic role. Roles are
    /// not linearly ordered (the authorization matrix is non-linear), so the
    /// membership aggregate exposes an exact-role check rather than a fabricated
    /// "at least role X" ladder. Per-action and object-level authorization that
    /// maps roles to capabilities is a later story (the workspace authorization
    /// policy stories) and is deliberately not built here.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    public bool HasRole(MembershipRole role)
    {
        if (!IsValidRole(role))
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Role is not a defined membership role.");
        }

        return Role == role;
    }

    /// <summary>
    /// Changes the generic role of this membership. The tenant boundary
    /// (organization) and the subject are immutable, so re-roling never moves
    /// the membership to another organization or subject (threat T5).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The new role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    public void ChangeRole(MembershipRole role, DateTimeOffset updatedAt)
    {
        if (!IsValidRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
        }

        Role = role;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a defined <see cref="MembershipRole"/>. Used
    /// to reject undefined enum values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidRole(MembershipRole role) => Enum.IsDefined(role);

    /// <summary>
    /// Identifier-only representation that is safe for structured logs:
    /// membership id, organization id, subject id and role. All four are
    /// identifiers/authorization metadata, never free-form content (threat
    /// T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"OrganizationMember {Id} org={OrganizationId} sub={UserProfileId} role={Role}";
}
