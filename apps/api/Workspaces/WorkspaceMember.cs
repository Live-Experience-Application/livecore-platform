using LiveCore.Api.Organizations;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Workspace membership aggregate of the Workspaces module (CORE-WS-002).
///
/// A <see cref="WorkspaceMember"/> is the Core-owned, product-neutral link that
/// records that one subject (a user) belongs to one <see cref="Workspace"/> with
/// one generic <see cref="MembershipRole"/> (docs/05_MODULE_CONTRACTS.md: the
/// Workspaces module owns "workspace membership" and "workspace-level roles";
/// csv/database_tables.csv: table <c>workspace_members</c>, module Workspaces,
/// scope <c>workspace</c>, "Workspace roles"). It is the direct workspace-level
/// analogue of <see cref="OrganizationMember"/>: later authorization policies
/// read this row to decide what a subject may do inside a workspace.
///
/// Role set: the workspace role is the SAME generic
/// <see cref="MembershipRole"/> the Organizations module uses. The authorization
/// matrix is a single shared matrix whose columns
/// (owner/admin/host/cohost/participant/observer/auditor) describe
/// workspace-level actions ("View workspace metadata", "Manage workspace
/// settings", "Manage members"); docs/06_AUTHORIZATION_MATRIX.md and
/// docs/01_PRODUCT_VISION_AND_SCOPE.md define one generic role list, and
/// ADR 0003 mandates one product-neutral role language. There is no distinct
/// workspace-specific role set to model, so this aggregate reuses the existing
/// enum rather than fabricating a parallel one.
///
/// Tenant boundary: the membership is workspace-scoped and, per
/// docs/10_DATABASE_SCHEMA.md (the documented critical index is
/// <c>workspace_members(workspace_id, user_id)</c>), keyed by
/// <see cref="WorkspaceId"/> and <see cref="UserProfileId"/>. The workspace
/// itself is tenant-scoped, so it already pins the organization; this row
/// additionally carries <see cref="OrganizationId"/> (the tenant) so isolation
/// is enforced at the row level and a workspace membership in organization A is
/// never resolved through organization B (docs/10_DATABASE_SCHEMA.md: principle
/// "tenant-scoped tables include <c>organization_id</c>"; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md, which calls for object-level
/// authorization to be "organization boundary checked before workspace
/// boundary"). A membership grants standing only in the workspace it names:
/// membership in workspace W1 says nothing about workspace W2. The subject is
/// referenced by the surrogate id of its user profile reference
/// (<see cref="UserProfileId"/>, the <c>user_id</c> foreign key to the
/// <c>users</c> table). No credentials are stored here: authentication is owned
/// by the external OIDC provider (ADR 0005).
///
/// Identity invariant: a subject has at most one membership per workspace. The
/// pair (<see cref="WorkspaceId"/>, <see cref="UserProfileId"/>) is the natural
/// key, immutable for the lifetime of the row, and enforced by a unique
/// database index. The surrogate <see cref="Id"/> is the row's own key.
///
/// Authorization invariant: <see cref="Role"/> is a generic role drawn from the
/// authorization matrix (docs/06_AUTHORIZATION_MATRIX.md). It is the only
/// authorization input on this aggregate; it is never inferred from the display
/// metadata of any other aggregate. The role can be changed
/// (<see cref="ChangeRole"/>) but the tenant boundary, the workspace and the
/// subject can not: re-roling never moves a membership to another organization,
/// another workspace or another subject. The matrix is non-linear, so authority
/// is decided by exact-role match (<see cref="HasRole"/>) or, later, by
/// per-action policies that consume the matrix; the role is never compared with
/// an ordering operator and is never interpreted into a capability here (that is
/// CORE-WS-005).
/// </summary>
public sealed class WorkspaceMember
{
    private WorkspaceMember(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
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

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
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
        WorkspaceId = workspaceId;
        UserProfileId = userProfileId;
        Role = role;
        // Timestamps are normalized to UTC so the persisted timestamptz
        // values are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private WorkspaceMember()
    {
    }

    /// <summary>
    /// Surrogate key of the membership row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). The natural key stays the
    /// (workspace, subject) pair.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the membership: the id of the organization that owns
    /// the workspace this membership belongs to (the <c>organization_id</c>
    /// foreign key to the <c>organizations</c> table). It mirrors the owning
    /// workspace's tenant and is immutable; a membership never crosses to
    /// another organization. The organization boundary is checked before the
    /// workspace boundary, so this column lets isolation be enforced at the row
    /// level (threat T5; docs/06_AUTHORIZATION_MATRIX.md authorization
    /// principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the membership: the id of the workspace this
    /// membership belongs to (the <c>workspace_id</c> foreign key to the
    /// <c>workspaces</c> table). Immutable; a membership never moves to another
    /// workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Subject of the membership: the surrogate id of the user profile
    /// reference (the <c>user_id</c> foreign key to the <c>users</c> table).
    /// Immutable; a membership never moves to another subject.
    /// </summary>
    public Guid UserProfileId { get; }

    /// <summary>
    /// Generic role the subject holds in this workspace, drawn from the
    /// authorization matrix (docs/06_AUTHORIZATION_MATRIX.md). The only
    /// authorization input on this aggregate.
    /// </summary>
    public MembershipRole Role { get; private set; }

    /// <summary>When this membership was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this membership was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new membership linking the given subject to the given workspace
    /// (owned by the given organization) with the given generic role.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or subject id is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    public static WorkspaceMember Create(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        MembershipRole role,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            userProfileId,
            role,
            createdAt,
            createdAt);

    /// <summary>
    /// Whether this membership belongs to exactly the given organization. Empty
    /// ids match nothing. This is the tenant-boundary check, checked before the
    /// workspace boundary: a membership grants standing only in the organization
    /// it names, never in any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this membership belongs to exactly the given workspace. Empty ids
    /// match nothing. This is the workspace-boundary check: a membership grants
    /// standing only in the workspace it names, never in any other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this membership belongs to exactly the given subject. Empty ids
    /// match nothing.
    /// </summary>
    public bool BelongsToSubject(Guid userProfileId)
        => userProfileId != Guid.Empty && UserProfileId == userProfileId;

    /// <summary>
    /// Whether this membership is the one for exactly the given
    /// (workspace, subject) pair. Both must match; an empty id matches nothing.
    /// A membership in another workspace, even for the same subject, returns
    /// <see langword="false"/> (threat T5).
    /// </summary>
    public bool Identifies(Guid workspaceId, Guid userProfileId)
        => BelongsToWorkspace(workspaceId) && BelongsToSubject(userProfileId);

    /// <summary>
    /// Whether this membership holds exactly the given generic role. Roles are
    /// not linearly ordered (the authorization matrix is non-linear), so the
    /// membership aggregate exposes an exact-role check rather than a fabricated
    /// "at least role X" ladder. Per-action and object-level authorization that
    /// maps roles to capabilities is a later story (the workspace authorization
    /// policy tests CORE-WS-005) and is deliberately not built here.
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
    /// (organization), the workspace and the subject are immutable, so re-roling
    /// never moves the membership to another organization, workspace or subject
    /// (threat T5).
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
    /// membership id, organization id, workspace id, subject id and role. All
    /// five are identifiers/authorization metadata, never free-form content
    /// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"WorkspaceMember {Id} org={OrganizationId} ws={WorkspaceId} sub={UserProfileId} role={Role}";
}
