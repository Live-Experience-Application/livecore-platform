// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Scenes;

/// <summary>
/// Scene aggregate of the Scenes module (CORE-SCENE-001).
///
/// A <see cref="Scene"/> is the Core-owned, product-neutral "ordered segment" of a
/// workspace (docs/03_DOMAIN_LANGUAGE.md: a scene is a "Segment of a session";
/// docs/05_MODULE_CONTRACTS.md: the Scenes module owns "scene metadata" and "scene
/// ordering"; csv/database_tables.csv: table <c>scenes</c>, module Scenes, scope
/// <c>workspace</c>, "Ordered segments"). It stores no vertical product language —
/// it is a generic scene only, never a vertical-specific segment term
/// (docs/03_DOMAIN_LANGUAGE.md forbids the vertical segment mappings in Core source;
/// docs/04_PRODUCT_BOUNDARIES.md; csv/forbidden_core_terms.csv).
/// Verticals map it to their own vocabulary in their UI; Core persists only the
/// generic resource.
///
/// Tenant boundary: the scene is workspace-scoped and tenant-scoped, so it carries
/// both <see cref="OrganizationId"/> (the tenant; checked before the workspace
/// boundary) and <see cref="WorkspaceId"/>, mirroring <c>Session</c> and
/// <c>Participant</c> (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables include
/// <c>organization_id</c>, workspace-scoped tables include <c>workspace_id</c>; the
/// documented critical index is <c>scenes(workspace_id, id)</c>, with NO
/// <c>session_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). A scene
/// belongs to exactly one organization and one workspace for its whole lifetime; it
/// never crosses to another tenant or workspace. The surrogate <see cref="Id"/>
/// alone never authorizes access: every lookup is scoped by
/// <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> so one workspace's
/// scene can never be reached through another workspace's or another tenant's id
/// (threat T5; T1 broken object-level authorization).
///
/// Workspace-prepared, not session-bound: a scene is a workspace-prepared segment,
/// so it carries NO <c>session_id</c> (csv/database_tables.csv scopes <c>scenes</c>
/// to <c>workspace</c>; the documented critical index is <c>scenes(workspace_id,
/// id)</c>; the create route is POST /api/v1/workspaces/{workspaceId}/scenes, a
/// later story). A session activates a scene through its "active scene pointer"
/// (docs/05_MODULE_CONTRACTS.md: the Sessions module owns the active scene pointer)
/// in a LATER story; coupling a scene to a session here would be a forward
/// dependency and is deliberately omitted.
///
/// Identity invariant: the surrogate <see cref="Id"/> (UUIDv7) is the row's own key
/// and the foreign-key target that scene-scoped tables (content blocks) will
/// reference. A scene has NO natural key: it is identified only by its surrogate id,
/// so there is deliberately no slug/title uniqueness — two scenes in one workspace
/// may share a title and stay fully isolated.
///
/// Display invariant: <see cref="Title"/> is human-facing metadata only — the
/// minimal display identity a scene needs to exist as an aggregate, the same
/// precedent as <c>Session.Title</c> and <c>Participant.DisplayName</c>. It is never
/// an identity, never an authorization input and never a tenant boundary: two scenes
/// may legitimately share a title while staying fully isolated because their
/// organization id, workspace id and id differ.
///
/// Ordering invariant (the heart of this story): <see cref="Order"/> is an explicit
/// non-negative integer position of the scene within its workspace
/// (docs/05_MODULE_CONTRACTS.md: the Scenes module owns "scene ordering"). The
/// aggregate takes an EXPLICIT order and validates only that it is non-negative; it
/// deliberately does NOT enforce a unique or contiguous order. Assigning a
/// contiguous/append-to-end order and shifting siblings on insert or reorder is the
/// later create/reorder ENDPOINT story's concern. Ordered retrieval is made
/// deterministic by the repository (ordering on the order column then the id, so ties
/// never produce a nondeterministic sequence), not by a database uniqueness
/// constraint. <see cref="Reorder"/> changes only the order and the update timestamp;
/// it never moves the scene across tenants or workspaces.
///
/// There is deliberately no lifecycle status here. The Scenes module owns scene
/// metadata, ordering and relationships (docs/05_MODULE_CONTRACTS.md) but documents
/// no scene lifecycle status, so a status enum would be speculative scope — a scene
/// is metadata + order only.
/// </summary>
public sealed class Scene
{
    /// <summary>
    /// Maximum accepted title length. The title is informational display metadata;
    /// longer values are rejected as hostile or broken input. Mirrors the
    /// <c>Session.Title</c> and <c>Participant.DisplayName</c> bound.
    /// </summary>
    public const int MaxTitleLength = 256;

    private Scene(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        string title,
        int order,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (!IsValidTitle(title))
        {
            throw new ArgumentException("Title violates the title invariants.", nameof(title));
        }

        if (!IsValidOrder(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Scene order must not be negative.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A scene cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        Title = title;
        Order = order;
        // Timestamps are normalized to UTC so the persisted timestamptz values
        // are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private Scene()
    {
        Title = null!;
    }

    /// <summary>
    /// Surrogate key of the scene row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). This is the value scene-scoped tables (content
    /// blocks) will reference through their <c>scene_id</c> column. On its own it
    /// never authorizes access: lookups are always scoped by
    /// <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the scene: the id of the organization that owns the
    /// workspace this scene belongs to (the <c>organization_id</c> foreign key to the
    /// <c>organizations</c> table). Immutable; a scene never crosses to another
    /// organization. The organization boundary is checked before the workspace
    /// boundary, so this column lets isolation be enforced at the row level (threat
    /// T5; docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the scene: the id of the workspace this scene belongs to
    /// (the <c>workspace_id</c> foreign key to the <c>workspaces</c> table).
    /// Immutable; a scene never moves to another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Human-readable display label of the scene — the minimal display identity a
    /// scene needs to exist as an aggregate (the same precedent as
    /// <c>Session.Title</c> and <c>Participant.DisplayName</c>). Informational
    /// metadata only: never an identity, never an authorization input, never a tenant
    /// boundary.
    /// </summary>
    public string Title { get; private set; }

    /// <summary>
    /// Explicit non-negative ordering position of the scene within its workspace
    /// (docs/05_MODULE_CONTRACTS.md: the Scenes module owns "scene ordering"). The
    /// aggregate stores an explicit order and only requires it to be non-negative; it
    /// deliberately does not enforce a unique or contiguous order. Deterministic
    /// ordered retrieval (by this order, then by id, so ties never produce a
    /// nondeterministic sequence) is the repository's job, and assigning a
    /// contiguous/append-to-end order plus shifting siblings is the later create/reorder
    /// endpoint story's job. The persisted column is named <c>scene_order</c> because a
    /// bare <c>order</c> is a SQL reserved word.
    /// </summary>
    public int Order { get; private set; }

    /// <summary>When this scene was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this scene was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new scene in the given workspace (owned by the given organization)
    /// at the given explicit order. The order must be non-negative; the caller (a
    /// later create endpoint) is responsible for choosing the position — for example
    /// appending to the end of the workspace's scene list. The aggregate enforces only
    /// the non-negativity invariant, not uniqueness or contiguity (the repository
    /// returns scenes in a deterministic order regardless).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty, or the title violates an
    /// invariant.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The order is negative.</exception>
    public static Scene Create(
        Guid organizationId,
        Guid workspaceId,
        string title,
        int order,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            title?.Trim() ?? string.Empty,
            order,
            createdAt,
            createdAt);

    /// <summary>
    /// Whether this scene belongs to exactly the given organization. Empty ids match
    /// nothing. This is the tenant-boundary check, checked before the workspace
    /// boundary: a scene belongs only to the organization it names, never to any other
    /// (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this scene belongs to exactly the given workspace. Empty ids match
    /// nothing. This is the workspace-boundary check: a scene belongs only to the
    /// workspace it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this scene is the one identified by the given id WITHIN the given
    /// organization and workspace. All three must match; an empty id matches nothing.
    /// A scene with the same id under a different tenant or workspace (which cannot
    /// happen for a single row but guards the caller's intent) returns
    /// <see langword="false"/>: the surrogate id is only ever honored inside its
    /// tenant and workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Renames the scene's display label. The organization, workspace, id and order
    /// are immutable here, so renaming never moves the scene, never changes the tenant
    /// boundary and never changes its position (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">The new title violates an invariant.</exception>
    public void Rename(string title, DateTimeOffset updatedAt)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (!IsValidTitle(trimmed))
        {
            throw new ArgumentException("Title violates the title invariants.", nameof(title));
        }

        Title = trimmed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Moves the scene to a new explicit non-negative order within its workspace
    /// (docs/05_MODULE_CONTRACTS.md: the Scenes module owns "scene ordering"). The
    /// organization, workspace, id and title are immutable here, so reordering changes
    /// only the position and the update timestamp; it never moves the scene to another
    /// tenant or workspace (threat T5). The aggregate enforces only that the new order
    /// is non-negative; shifting sibling scenes to keep orders contiguous/unique is the
    /// later reorder endpoint story's concern.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The new order is negative.</exception>
    public void Reorder(int order, DateTimeOffset updatedAt)
    {
        if (!IsValidOrder(order))
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Scene order must not be negative.");
        }

        Order = order;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid title: non-blank, within the length bound
    /// and free of control characters.
    /// </summary>
    public static bool IsValidTitle(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxTitleLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a valid scene order: a non-negative position. A
    /// negative order is meaningless as a list position and is rejected.
    /// </summary>
    public static bool IsValidOrder(int order) => order >= 0;

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: scene id,
    /// organization id, workspace id and order. The title is excluded so it cannot
    /// leak through logs (threat T7 in docs/07_SECURITY_THREAT_MODEL.md: logs carry
    /// identifiers, not free-form metadata content).
    /// </summary>
    public override string ToString()
        => $"Scene {Id} org={OrganizationId} ws={WorkspaceId} order={Order}";
}
