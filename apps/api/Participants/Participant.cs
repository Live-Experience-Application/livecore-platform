namespace LiveCore.Api.Participants;

/// <summary>
/// Participant aggregate of the Participants module (CORE-SES-001).
///
/// A <see cref="Participant"/> is the Core-owned, product-neutral
/// "session-facing person or role" of a workspace (docs/03_DOMAIN_LANGUAGE.md;
/// docs/05_MODULE_CONTRACTS.md: the Participants module owns "session/workspace
/// participant records" and "participant display identity";
/// csv/database_tables.csv: table <c>participants</c>, module Participants, scope
/// <c>workspace</c>, "Session-facing participant"). It stores no vertical product
/// language — it is a generic participant only, never a vertical-specific actor
/// term (docs/04_PRODUCT_BOUNDARIES.md, csv/forbidden_core_terms.csv, ADR 0003:
/// the Participants module may not assume a participant means any vertical-
/// specific actor).
/// Verticals map it to their own vocabulary in their UI; Core persists only the
/// generic resource.
///
/// User link is OPTIONAL. docs/03_DOMAIN_LANGUAGE.md defines a participant as a
/// "Session-facing person OR role, MAY be linked to a user". A participant linked
/// to a <see cref="UserProfileId"/> is an authenticated user acting in the
/// session; a participant with no user link is an anonymous/guest actor that has
/// no user account. This is distinct from <c>WorkspaceMember</c>, which always
/// links an authenticated user to a workspace with an authorization-matrix role:
/// a participant carries display identity and lifecycle, NOT a membership role.
/// The authorization-matrix <c>MembershipRole</c> governs what an authenticated
/// member may DO in a workspace (docs/06_AUTHORIZATION_MATRIX.md); it is
/// deliberately not carried here because the docs model a participant as a
/// session-facing identity (display name + status), not as a member-role
/// assignment, and an anonymous participant has no membership at all.
///
/// Tenant boundary: the participant is workspace-scoped and tenant-scoped, so it
/// carries both <see cref="OrganizationId"/> (the tenant; checked before the
/// workspace boundary) and <see cref="WorkspaceId"/>, mirroring
/// <c>WorkspaceMember</c> (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables
/// include <c>organization_id</c>, workspace-scoped tables include
/// <c>workspace_id</c>; the documented critical index is
/// <c>participants(workspace_id, id)</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). A participant belongs to exactly one
/// organization and one workspace for its whole lifetime; it never crosses to
/// another tenant or workspace. The surrogate <see cref="Id"/> alone never
/// authorizes access: every lookup is scoped by <see cref="OrganizationId"/> and
/// <see cref="WorkspaceId"/> so one workspace's participant can never be reached
/// through another workspace's or another tenant's id (threat T5; T1 broken
/// object-level authorization).
///
/// Identity invariant: the surrogate <see cref="Id"/> (UUIDv7) is the row's own
/// key. When the participant is linked to a user, the
/// (<see cref="WorkspaceId"/>, <see cref="UserProfileId"/>) pair is a natural key
/// enforced by a unique database index: one user is at most one participant per
/// workspace. Anonymous participants (no user link) are not constrained by that
/// uniqueness, so a workspace may hold many anonymous participants; each is
/// addressed only by its surrogate id.
///
/// Display invariant: <see cref="DisplayName"/> is human-facing metadata only. It
/// is never an identity, never an authorization input and never a tenant
/// boundary: two participants may legitimately share a display name while staying
/// fully isolated because their organization id, workspace id and id differ.
///
/// Lifecycle invariant: a participant is created <see cref="ParticipantStatus.Active"/>
/// and is removed by a status transition (<see cref="Remove"/>), a soft delete
/// (docs/10_DATABASE_SCHEMA.md: avoid hard-delete for business data). Removal
/// never moves the participant to another tenant, workspace or subject.
/// </summary>
public sealed class Participant
{
    /// <summary>
    /// Maximum accepted display-name length. The display name is informational
    /// metadata; longer values are rejected as hostile or broken input. Mirrors
    /// the user-profile display-name bound.
    /// </summary>
    public const int MaxDisplayNameLength = 256;

    private Participant(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid? userProfileId,
        string displayName,
        ParticipantStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Participant id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The user link is optional (docs/03: "may be linked to a user"), but
        // when present it must be a real, non-empty surrogate id: an explicit
        // empty user link is neither a valid user nor a valid anonymous
        // participant, so it is rejected rather than coerced.
        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A linked user profile id must not be empty; pass null for an anonymous participant.",
                nameof(userProfileId));
        }

        if (!IsValidDisplayName(displayName))
        {
            throw new ArgumentException("Display name violates the display name invariants.", nameof(displayName));
        }

        if (!IsValidStatus(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Status is not a defined participant status.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A participant cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        UserProfileId = userProfileId;
        DisplayName = displayName;
        Status = status;
        // Timestamps are normalized to UTC so the persisted timestamptz values
        // are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private Participant()
    {
        DisplayName = null!;
    }

    /// <summary>
    /// Surrogate key of the participant row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). This is the value session-facing tables will
    /// reference. On its own it never authorizes access: lookups are always
    /// scoped by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/>
    /// (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the participant: the id of the organization that owns
    /// the workspace this participant belongs to (the <c>organization_id</c>
    /// foreign key to the <c>organizations</c> table). Immutable; a participant
    /// never crosses to another organization. The organization boundary is
    /// checked before the workspace boundary, so this column lets isolation be
    /// enforced at the row level (threat T5; docs/06_AUTHORIZATION_MATRIX.md
    /// authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the participant: the id of the workspace this
    /// participant belongs to (the <c>workspace_id</c> foreign key to the
    /// <c>workspaces</c> table). Immutable; a participant never moves to another
    /// workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Optional link to the authenticated user this participant represents (the
    /// <c>user_id</c> foreign key to the <c>users</c> table).
    /// <see langword="null"/> for an anonymous/guest participant that has no user
    /// account (docs/03_DOMAIN_LANGUAGE.md: a participant "may be linked to a
    /// user"). Immutable; a participant never moves to another subject.
    /// </summary>
    public Guid? UserProfileId { get; }

    /// <summary>
    /// Human-readable display identity of the participant
    /// (docs/05_MODULE_CONTRACTS.md: the Participants module owns "participant
    /// display identity"). Informational metadata only: never an identity, never
    /// an authorization input, never a tenant boundary.
    /// </summary>
    public string DisplayName { get; private set; }

    /// <summary>
    /// Lifecycle status of the participant. A participant is created
    /// <see cref="ParticipantStatus.Active"/> and removed by a soft delete
    /// (<see cref="Remove"/>).
    /// </summary>
    public ParticipantStatus Status { get; private set; }

    /// <summary>When this participant was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this participant was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Whether this participant is linked to a user account. <see langword="false"/>
    /// for an anonymous/guest participant.
    /// </summary>
    public bool IsLinkedToUser => UserProfileId is not null;

    /// <summary>
    /// Creates a new active participant in the given workspace (owned by the
    /// given organization), optionally linked to a user. Pass
    /// <paramref name="userProfileId"/> as <see langword="null"/> for an
    /// anonymous/guest participant (docs/03_DOMAIN_LANGUAGE.md). The participant
    /// starts <see cref="ParticipantStatus.Active"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty, a supplied user profile id
    /// is empty, or the display name violates an invariant.
    /// </exception>
    public static Participant Create(
        Guid organizationId,
        Guid workspaceId,
        Guid? userProfileId,
        string displayName,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            userProfileId,
            displayName?.Trim() ?? string.Empty,
            ParticipantStatus.Active,
            createdAt,
            createdAt);

    /// <summary>
    /// Whether this participant belongs to exactly the given organization. Empty
    /// ids match nothing. This is the tenant-boundary check, checked before the
    /// workspace boundary: a participant belongs only to the organization it
    /// names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this participant belongs to exactly the given workspace. Empty ids
    /// match nothing. This is the workspace-boundary check: a participant belongs
    /// only to the workspace it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this participant is linked to exactly the given user. Empty ids
    /// match nothing, and an anonymous participant (no user link) matches no user
    /// id at all.
    /// </summary>
    public bool BelongsToSubject(Guid userProfileId)
        => userProfileId != Guid.Empty && UserProfileId == userProfileId;

    /// <summary>
    /// Whether this participant is the one identified by the given id WITHIN the
    /// given organization and workspace. All three must match; an empty id
    /// matches nothing. A participant with the same id under a different tenant or
    /// workspace (which cannot happen for a single row but guards the caller's
    /// intent) returns <see langword="false"/>: the surrogate id is only ever
    /// honored inside its tenant and workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Renames the participant's display identity. The organization, workspace,
    /// user link and id are immutable, so renaming never moves the participant,
    /// never changes the tenant boundary and never changes the natural key
    /// (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">The new display name violates an invariant.</exception>
    public void Rename(string displayName, DateTimeOffset updatedAt)
    {
        var trimmed = displayName?.Trim() ?? string.Empty;
        if (!IsValidDisplayName(trimmed))
        {
            throw new ArgumentException("Display name violates the display name invariants.", nameof(displayName));
        }

        DisplayName = trimmed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Removes the participant from its workspace (a soft delete): the status
    /// moves to <see cref="ParticipantStatus.Removed"/>. The organization,
    /// workspace, user link and id are immutable, so removal never moves the
    /// participant (threat T5). Removing an already-removed participant is a
    /// no-op rather than an error, so the operation is idempotent.
    /// </summary>
    public void Remove(DateTimeOffset updatedAt)
    {
        if (Status == ParticipantStatus.Removed)
        {
            return;
        }

        Status = ParticipantStatus.Removed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid display name: non-blank, within the
    /// length bound and free of control characters.
    /// </summary>
    public static bool IsValidDisplayName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxDisplayNameLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a defined <see cref="ParticipantStatus"/>. Used
    /// to reject undefined enum values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidStatus(ParticipantStatus status) => Enum.IsDefined(status);

    /// <summary>
    /// Identifier-only representation that is safe for structured logs:
    /// participant id, organization id, workspace id, user link (or
    /// <c>anonymous</c>) and status. The display name is excluded so it cannot
    /// leak through logs (threat T7 in docs/07_SECURITY_THREAT_MODEL.md: logs
    /// carry identifiers, not free-form metadata content).
    /// </summary>
    public override string ToString()
        => $"Participant {Id} org={OrganizationId} ws={WorkspaceId} "
            + $"user={(UserProfileId is { } user ? user.ToString() : "anonymous")} status={Status}";
}
