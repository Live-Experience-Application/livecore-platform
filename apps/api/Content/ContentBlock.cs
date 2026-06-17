// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Content;

/// <summary>
/// ContentBlock aggregate of the Content module (CORE-SCENE-002).
///
/// A <see cref="ContentBlock"/> is the Core-owned, product-neutral "Text/media/data
/// unit shown or hidden by visibility rules" (docs/03_DOMAIN_LANGUAGE.md;
/// docs/05_MODULE_CONTRACTS.md: the Content module owns "content blocks", "content
/// block revisions" and a "content type registry"; csv/database_tables.csv: table
/// <c>content_blocks</c>, module Content, scope <c>workspace</c>, "Text/media/data
/// block"). It stores no vertical product language — it is a generic content unit
/// only (docs/04_PRODUCT_BOUNDARIES.md, csv/forbidden_core_terms.csv). Verticals map
/// it to their own vocabulary in their UI; Core persists only the generic resource
/// (docs/04: public contract <c>/content-blocks</c>).
///
/// Visibility boundary: a content block carries NO visibility logic. The Content
/// module "may not decide visibility alone; must use Visibility module"
/// (docs/05_MODULE_CONTRACTS.md), and visibility is the central security module's
/// concern (the <c>visibility_rules</c> table; threats T2/T3). So this aggregate is
/// only the host-prepared content unit and its revisions; whether a participant may
/// see it is computed server-side by the Visibility module in a later epic, never
/// here. The host-vs-participant DTO separation is the CORE-SCENE-004 story; per-type
/// content validation and explicit size limits are enforced via
/// <see cref="ContentValidator"/> (CORE-SCENE-005) and must NEVER become a visibility
/// decision.
///
/// Tenant + scene boundary: a content block is workspace-scoped (csv/database_tables.csv
/// scopes <c>content_blocks</c> to <c>workspace</c>) AND scene-scoped — the documented
/// critical index is <c>content_blocks(workspace_id, scene_id)</c>
/// (docs/10_DATABASE_SCHEMA.md). So it carries <see cref="OrganizationId"/> (the
/// tenant; checked before the workspace boundary), <see cref="WorkspaceId"/> and
/// <see cref="SceneId"/>, mirroring how <c>Scene</c> carries the org+workspace pair
/// and adding the scene the block belongs to (docs/10_DATABASE_SCHEMA.md: tenant-scoped
/// tables include <c>organization_id</c>, workspace-scoped tables include
/// <c>workspace_id</c>, session-/scene-scoped tables include their parent id; threat
/// T5 in docs/07_SECURITY_THREAT_MODEL.md). A content block belongs to exactly one
/// organization, one workspace and one scene for its whole lifetime; it never crosses
/// to another tenant, workspace or scene. The surrogate <see cref="Id"/> alone never
/// authorizes access: every lookup is scoped by <see cref="OrganizationId"/>,
/// <see cref="WorkspaceId"/> (and the scene where relevant) so one scene's content
/// block can never be reached through another workspace's or another tenant's id
/// (threat T5; T1 broken object-level authorization).
///
/// Identity invariant: the surrogate <see cref="Id"/> (UUIDv7) is the row's own key.
/// A content block has NO natural key: it is identified only by its surrogate id, so
/// there is deliberately no slug/title uniqueness — two content blocks in one scene
/// may share a body and stay fully isolated.
///
/// Body invariant: <see cref="Body"/> is the content payload — text, a media
/// reference or a structured data document depending on <see cref="Type"/>. It is
/// host-prepared content, so it is treated as sensitive (it is never written to logs;
/// see <see cref="ToString"/>, threat T7). The body is validated PER TYPE by
/// <see cref="ContentValidator"/> (CORE-SCENE-005): Text is bounded plain text, Media a
/// bounded reference string, and Data a bounded, well-formed JSON document, each with its
/// own explicit size limit. Both <see cref="Create"/> and <see cref="Revise"/> reject an
/// invalid or oversize body for the block's type with NO mutation, so a rejected revision
/// leaves the prior revision intact.
///
/// Revision invariant (the headline feature): <see cref="RevisionNumber"/> is an
/// explicit monotonically increasing version of the block's body, starting at
/// <see cref="InitialRevisionNumber"/> (1) on creation. <see cref="Revise"/> replaces
/// the current body (and may re-type the block) and increments the revision number by
/// exactly one; it never decrements and never skips. This realizes the Content
/// module's "content block revisions" responsibility (docs/05_MODULE_CONTRACTS.md)
/// while honoring the database principles in docs/10_DATABASE_SCHEMA.md ("avoid
/// hard-delete for business data; use soft delete where needed"; "use optimistic
/// concurrency where needed"): the body is versioned in place rather than being
/// destructively overwritten without a trace. csv/database_tables.csv lists NO
/// separate <c>content_block_revisions</c> table — only <c>content_blocks</c> — so
/// the revision is modelled as an explicit version counter on the single aggregate;
/// retaining a full immutable history of every prior body in a dedicated revisions
/// table (with its own append-only rows) is a natural follow-up if a story ever
/// requires reading past bodies, and is flagged to the backlog owner rather than
/// inventing a table here (the orchestrator owns csv/docs schema additions). The
/// monotonic revision counter is also the concurrency token a later optimistic-
/// concurrency check can build on.
///
/// There is deliberately no lifecycle status here. The Content module owns content
/// blocks, their revisions and a content type registry (docs/05_MODULE_CONTRACTS.md)
/// but documents no content block lifecycle status, so a status enum would be
/// speculative scope — a content block is a typed body plus its revision number.
/// </summary>
public sealed class ContentBlock
{
    /// <summary>
    /// The revision number a content block carries when it is first created. The
    /// first version of the body is revision 1; the counter increases by one on every
    /// <see cref="Revise"/>.
    /// </summary>
    public const int InitialRevisionNumber = 1;

    /// <summary>
    /// Overall maximum accepted body length across all content types — the maximum of the
    /// per-type limits in <see cref="ContentValidator"/> (re-exposed here so the
    /// persistence column bound and any caller can reference it from the aggregate). The
    /// real, narrower, type-specific size limits live on <see cref="ContentValidator"/>
    /// (CORE-SCENE-005); this column-level bound equals the size the table was created
    /// with, so no schema migration is required.
    /// </summary>
    public const int MaxBodyLength = ContentValidator.MaxBodyLength;

    private ContentBlock(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        ContentBlockType type,
        string body,
        int revisionNumber,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Content block id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sceneId == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(sceneId));
        }

        if (!IsValidType(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Type is not a defined content block type.");
        }

        // Per-type content validation and size limits (CORE-SCENE-005): the body must be
        // valid for THIS block's type (Text/Media/Data), not merely non-blank. The
        // validator never throws and never decides visibility; it only judges
        // well-formedness and the per-type size bound.
        if (!ContentValidator.IsValidBody(type, body))
        {
            throw new ArgumentException("Body violates the body invariants for its content type.", nameof(body));
        }

        if (!IsValidRevisionNumber(revisionNumber))
        {
            throw new ArgumentOutOfRangeException(
                nameof(revisionNumber),
                revisionNumber,
                "Revision number must be at least the initial revision number.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A content block cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        SceneId = sceneId;
        Type = type;
        Body = body;
        RevisionNumber = revisionNumber;
        // Timestamps are normalized to UTC so the persisted timestamptz values
        // are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private ContentBlock()
    {
        Body = null!;
    }

    /// <summary>
    /// Surrogate key of the content block row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). On its own it never authorizes access: lookups are
    /// always scoped by <see cref="OrganizationId"/>, <see cref="WorkspaceId"/> and
    /// the scene (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the content block: the id of the organization that owns the
    /// workspace this block belongs to (the <c>organization_id</c> foreign key to the
    /// <c>organizations</c> table). Immutable; a content block never crosses to
    /// another organization. The organization boundary is checked before the workspace
    /// boundary, so this column lets isolation be enforced at the row level (threat
    /// T5; docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the content block: the id of the workspace this block
    /// belongs to (the <c>workspace_id</c> foreign key to the <c>workspaces</c>
    /// table). Immutable; a content block never moves to another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Scene boundary of the content block: the id of the scene this block belongs to
    /// (the <c>scene_id</c> foreign key to the <c>scenes</c> table), the documented
    /// <c>content_blocks(workspace_id, scene_id)</c> shape (docs/10_DATABASE_SCHEMA.md
    /// critical indexes). Immutable; a content block never moves to another scene
    /// (threat T5).
    /// </summary>
    public Guid SceneId { get; }

    /// <summary>
    /// Generic kind of the block — Text, Media or Data (docs/03_DOMAIN_LANGUAGE.md).
    /// May be re-set by <see cref="Revise"/> when a new revision replaces the body
    /// with a different kind of content.
    /// </summary>
    public ContentBlockType Type { get; private set; }

    /// <summary>
    /// The content payload of the current revision — text, a media reference or a
    /// structured data document depending on <see cref="Type"/>. Host-prepared,
    /// sensitive content: never an identity, never an authorization input, never a
    /// tenant boundary, and never written to logs (threat T7; see
    /// <see cref="ToString"/>).
    /// </summary>
    public string Body { get; private set; }

    /// <summary>
    /// Monotonically increasing version of the body, starting at
    /// <see cref="InitialRevisionNumber"/> (1) on creation and incremented by exactly
    /// one on every <see cref="Revise"/> (docs/05_MODULE_CONTRACTS.md: the Content
    /// module owns "content block revisions"). It never decreases and never skips, so
    /// it is both the revision count and a monotonic concurrency token a later
    /// optimistic-concurrency check can build on (docs/10_DATABASE_SCHEMA.md).
    /// </summary>
    public int RevisionNumber { get; private set; }

    /// <summary>When this content block was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this content block was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new content block in the given scene (of the given workspace, owned
    /// by the given organization) at the initial revision
    /// (<see cref="InitialRevisionNumber"/>). The caller (a later create endpoint,
    /// CORE-SCENE-003) supplies the tenant, workspace and scene ids; the aggregate
    /// enforces only the structural invariants (non-empty ids, a defined type, a valid
    /// body).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or scene id is empty, or the body violates an
    /// invariant.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The type is not defined.</exception>
    public static ContentBlock Create(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        ContentBlockType type,
        string body,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            sceneId,
            type,
            body?.Trim() ?? string.Empty,
            InitialRevisionNumber,
            createdAt,
            createdAt);

    /// <summary>
    /// Whether this content block belongs to exactly the given organization. Empty ids
    /// match nothing. This is the tenant-boundary check, checked before the workspace
    /// boundary: a content block belongs only to the organization it names, never to
    /// any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this content block belongs to exactly the given workspace. Empty ids
    /// match nothing. This is the workspace-boundary check: a content block belongs
    /// only to the workspace it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this content block belongs to exactly the given scene. Empty ids match
    /// nothing. This is the scene-boundary check: a content block belongs only to the
    /// scene it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToScene(Guid sceneId)
        => sceneId != Guid.Empty && SceneId == sceneId;

    /// <summary>
    /// Whether this content block is the one identified by the given id WITHIN the
    /// given organization and workspace. All three must match; an empty id matches
    /// nothing. A content block with the same id under a different tenant or workspace
    /// (which cannot happen for a single row but guards the caller's intent) returns
    /// <see langword="false"/>: the surrogate id is only ever honored inside its tenant
    /// and workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Creates a new revision of the content block: replaces the current body (and may
    /// re-type the block) and increments <see cref="RevisionNumber"/> by exactly one
    /// (docs/05_MODULE_CONTRACTS.md: the Content module owns "content block revisions").
    /// The organization, workspace, scene and id are immutable here, so revising never
    /// moves the block across tenants, workspaces or scenes (threat T5); it changes only
    /// the type, the body, the revision number and the update timestamp. The new body
    /// must satisfy the same body invariants, and the new type must be defined; an
    /// invalid revision is rejected with NO mutation, so the prior revision's body and
    /// number are left intact.
    /// </summary>
    /// <exception cref="ArgumentException">The new body violates an invariant.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The new type is not defined.</exception>
    public void Revise(ContentBlockType type, string body, DateTimeOffset updatedAt)
    {
        if (!IsValidType(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Type is not a defined content block type.");
        }

        // Per-type content validation and size limits (CORE-SCENE-005): the NEW body must be
        // valid for the NEW type. An invalid/oversize revision is rejected here with NO
        // mutation, so the prior revision's type, body and number are left intact
        // (prior-revision immutability).
        var trimmed = body?.Trim() ?? string.Empty;
        if (!ContentValidator.IsValidBody(type, trimmed))
        {
            throw new ArgumentException("Body violates the body invariants for its content type.", nameof(body));
        }

        Type = type;
        Body = trimmed;
        RevisionNumber++;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a defined <see cref="ContentBlockType"/>. Used to
    /// reject undefined enum values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidType(ContentBlockType type) => Enum.IsDefined(type);

    /// <summary>
    /// Whether the given value is a valid revision number: at least the initial
    /// revision number (1). A revision number below 1 is meaningless and is rejected.
    /// </summary>
    public static bool IsValidRevisionNumber(int revisionNumber)
        => revisionNumber >= InitialRevisionNumber;

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: content block
    /// id, organization id, workspace id, scene id, type and revision number. The body
    /// is excluded so host-prepared content cannot leak through logs (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not sensitive body
    /// content).
    /// </summary>
    public override string ToString()
        => $"ContentBlock {Id} org={OrganizationId} ws={WorkspaceId} scene={SceneId} "
            + $"type={Type} rev={RevisionNumber}";
}
