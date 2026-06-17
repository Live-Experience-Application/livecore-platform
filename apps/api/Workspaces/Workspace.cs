// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.RegularExpressions;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Workspace aggregate of the Workspaces module (CORE-WS-001).
///
/// A <see cref="Workspace"/> is the Core-owned, product-neutral "container for a
/// live experience" (docs/03_DOMAIN_LANGUAGE.md; docs/05_MODULE_CONTRACTS.md:
/// the Workspaces module owns "workspace metadata"; csv/database_tables.csv:
/// table <c>workspaces</c>, module Workspaces, scope <c>organization</c>,
/// "Generic workspace"). It stores no vertical product language — it is a
/// generic workspace only, never a vertical-specific container term
/// (docs/04_PRODUCT_BOUNDARIES.md, csv/forbidden_core_terms.csv, ADR 0003).
/// Verticals map it to their own vocabulary in their UI; Core persists only the
/// generic resource (docs/04: public contract <c>/workspaces</c>).
///
/// Instances are only ever in a valid state: the constructor rejects every
/// argument that violates an invariant, mirroring <see cref="Workspace"/>'s
/// closest analogue, the Organization aggregate (CORE-ID-003).
///
/// Tenant boundary: the workspace is tenant-scoped, so it carries
/// <see cref="OrganizationId"/> (docs/10_DATABASE_SCHEMA.md: tenant-scoped
/// tables include <c>organization_id</c>; the documented critical index leads
/// with <c>organization_id</c>: <c>workspaces(organization_id, id)</c>; threat
/// T5 in docs/07_SECURITY_THREAT_MODEL.md). A workspace belongs to exactly one
/// organization for its whole lifetime; it never crosses to another tenant. The
/// surrogate <see cref="Id"/> alone never authorizes access: every lookup is
/// scoped by <see cref="OrganizationId"/> so one tenant's workspace can never be
/// reached through another tenant's id (threat T5; T1 broken object-level
/// authorization).
///
/// Identity invariant: a workspace is addressed inside its organization by two
/// keys. The surrogate <see cref="Id"/> (UUIDv7) is the row's own key and the
/// foreign-key target workspace-scoped tables will reference through their
/// <c>workspace_id</c> (docs/10_DATABASE_SCHEMA.md). The <see cref="Slug"/> is
/// the stable, human-meaning natural key, unique WITHIN the organization (not
/// globally): the same slug may legitimately exist in two different
/// organizations, but never twice in one. The slug is immutable, lower-case and
/// canonical, so a near-match or re-used slug can never silently address a
/// different workspace. All slug comparisons are exact (ordinal,
/// case-sensitive); the lower-casing happens once at creation so the persisted
/// value is already canonical.
///
/// Display invariant: <see cref="Name"/> is human-facing metadata only. It is
/// never an identity, never an authorization input, and never a tenant
/// boundary: two workspaces may legitimately share a display name while staying
/// fully isolated because their organization id, slug and id differ.
///
/// Lifecycle invariant: a workspace is created <see cref="WorkspaceStatus.Active"/>
/// and is taken out of active use by a status transition (<see cref="Archive"/>),
/// a SOFT archive rather than a hard delete (CORE-LIFE-009;
/// docs/10_DATABASE_SCHEMA.md: avoid hard-delete for business data). Archiving
/// preserves the workspace and all of its children — sessions, scenes, content,
/// entities, assets and the append-only audit trail — and only flips the status;
/// it never moves the workspace to another tenant. Archive is the clearly
/// TERMINAL end-state for this story: there is no un-archive transition, and an
/// already-archived workspace is never archived again.
/// </summary>
public sealed class Workspace
{
    /// <summary>
    /// Minimum slug length. A single-character workspace key is too easy to
    /// collide with or mistype, so the shortest accepted slug is two
    /// characters (mirrors <c>Organization.MinSlugLength</c>).
    /// </summary>
    public const int MinSlugLength = 2;

    /// <summary>
    /// Maximum slug length. The slug is part of URLs and per-tenant lookups;
    /// this bound keeps it well within identifier and index limits.
    /// </summary>
    public const int MaxSlugLength = 64;

    /// <summary>
    /// Maximum accepted display name length. Names are informational
    /// metadata; longer values are rejected as hostile or broken input.
    /// </summary>
    public const int MaxNameLength = 256;

    // Canonical slug shape: lower-case letters/digits in dash-separated groups
    // (no leading, trailing or doubled dash). This keeps the workspace key
    // URL-safe, predictable and free of control characters or casing ambiguity
    // that could blur per-tenant identity (threat T5). Identical to the
    // Organization slug shape so the two natural keys behave consistently.
    private static readonly Regex _slugPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private Workspace(
        Guid id,
        Guid organizationId,
        string slug,
        string name,
        WorkspaceStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (!IsValidSlug(slug))
        {
            throw new ArgumentException("Slug violates the slug invariants.", nameof(slug));
        }

        if (!IsValidName(name))
        {
            throw new ArgumentException("Name violates the name invariants.", nameof(name));
        }

        if (!IsValidStatus(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Status is not a defined workspace status.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A workspace cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        Slug = slug;
        Name = name;
        Status = status;
        // Timestamps are normalized to UTC so the persisted timestamptz values
        // are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private Workspace()
    {
        Slug = null!;
        Name = null!;
    }

    /// <summary>
    /// Surrogate key of the workspace row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). This is the value workspace-scoped tables
    /// reference through their <c>workspace_id</c> column. On its own it never
    /// authorizes access: lookups are always scoped by
    /// <see cref="OrganizationId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the workspace: the id of the organization this
    /// workspace belongs to (the <c>organization_id</c> foreign key to the
    /// <c>organizations</c> table). Immutable; a workspace never crosses to
    /// another organization (threat T5).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Stable, lower-case natural key of the workspace, unique WITHIN its
    /// organization (not globally). Immutable, URL-safe and used for per-tenant
    /// lookups. Comparisons are exact (ordinal, case-sensitive).
    /// </summary>
    public string Slug { get; }

    /// <summary>
    /// Human-readable display name of the workspace. Informational metadata
    /// only: never an identity, never an authorization input, never a tenant
    /// boundary.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Lifecycle status of the workspace (CORE-LIFE-009). A workspace is created
    /// <see cref="WorkspaceStatus.Active"/> and is taken out of active use by the
    /// soft, terminal <see cref="Archive"/> transition to
    /// <see cref="WorkspaceStatus.Archived"/>.
    /// </summary>
    public WorkspaceStatus Status { get; private set; }

    /// <summary>When this workspace was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this workspace was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Whether this workspace is archived (read-only and excluded from active
    /// lists; CORE-LIFE-009).
    /// </summary>
    public bool IsArchived => Status == WorkspaceStatus.Archived;

    /// <summary>
    /// Whether this workspace may be archived right now: a workspace may be
    /// archived only from <see cref="WorkspaceStatus.Active"/>. Lets the
    /// application layer branch (returning a 409 conflict for an already-archived
    /// workspace) without catching the guard exception in <see cref="Archive"/>.
    /// </summary>
    public bool CanArchive => Status == WorkspaceStatus.Active;

    /// <summary>
    /// Creates a new workspace in the given organization. The slug is
    /// canonicalized to lower case once here so the persisted value and every
    /// later comparison stay consistent; an already-canonical slug is
    /// unchanged. The workspace starts <see cref="WorkspaceStatus.Active"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id is empty, or the slug or name violates an invariant.
    /// </exception>
    public static Workspace Create(
        Guid organizationId,
        string slug,
        string name,
        DateTimeOffset createdAt)
    {
        var canonicalSlug = CanonicalizeSlug(slug);

        return new Workspace(
            Guid.CreateVersion7(),
            organizationId,
            canonicalSlug,
            name?.Trim() ?? string.Empty,
            WorkspaceStatus.Active,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Whether this workspace belongs to exactly the given organization. Empty
    /// ids match nothing. This is the tenant-boundary check: a workspace is
    /// reachable only through the organization it names, never through any
    /// other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this workspace is the one identified by the given id WITHIN the
    /// given organization. Both must match; an empty id matches nothing. A
    /// workspace with the same id but a different organization (which cannot
    /// happen for a single row but guards the caller's intent) returns
    /// <see langword="false"/>: the surrogate id is only ever honored inside
    /// its tenant (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid id)
        => BelongsToOrganization(organizationId) && id != Guid.Empty && Id == id;

    /// <summary>
    /// Whether this workspace is addressed by exactly the given slug WITHIN the
    /// given organization. Matching is exact (ordinal, case-sensitive) against
    /// the stored canonical slug; a blank or null slug, or a different
    /// organization, matches nothing. The same slug in another organization is
    /// a different workspace and is never matched here (threat T5).
    /// </summary>
    public bool HasSlugIn(Guid organizationId, string? slug)
        => BelongsToOrganization(organizationId)
            && !string.IsNullOrWhiteSpace(slug)
            && string.Equals(Slug, slug, StringComparison.Ordinal);

    /// <summary>
    /// Updates the display name. The organization, slug and id are immutable,
    /// so renaming never moves the workspace, never changes the tenant boundary
    /// and never changes the natural key (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">The new name violates an invariant.</exception>
    public void Rename(string name, DateTimeOffset updatedAt)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (!IsValidName(trimmedName))
        {
            throw new ArgumentException("Name violates the name invariants.", nameof(name));
        }

        Name = trimmedName;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Archives the workspace (CORE-LIFE-009): transitions
    /// <see cref="WorkspaceStatus.Active"/> -&gt; <see cref="WorkspaceStatus.Archived"/>
    /// and stamps <see cref="UpdatedAt"/>. This is a SOFT archive — the workspace
    /// and all of its children are preserved; only the status changes — and a
    /// CLEARLY TERMINAL end-state: there is no un-archive transition. The
    /// organization, slug and id are immutable, so archiving never moves the
    /// workspace to another tenant (threat T5). Archiving an already-archived
    /// workspace is an invalid transition, NOT a no-op: the application layer
    /// pre-checks <see cref="CanArchive"/> and reports a 409 conflict, so a second
    /// archive never writes a duplicate audit fact for a state that did not change.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The workspace is not <see cref="WorkspaceStatus.Active"/> (it is already
    /// archived). This is a defensive guard; callers pre-check
    /// <see cref="CanArchive"/>.
    /// </exception>
    public void Archive(DateTimeOffset updatedAt)
    {
        if (!CanArchive)
        {
            throw new InvalidOperationException("Only an active workspace can be archived.");
        }

        Status = WorkspaceStatus.Archived;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid canonical slug: lower-case,
    /// dash-separated alphanumeric groups, within the length bounds and free of
    /// control characters. Validation does not mutate; callers pass an
    /// already-canonical value or use <see cref="CanonicalizeSlug"/> first.
    /// </summary>
    public static bool IsValidSlug(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length >= MinSlugLength
            && value.Length <= MaxSlugLength
            && _slugPattern.IsMatch(value);

    /// <summary>
    /// Whether the given value is a valid display name: non-blank, within the
    /// length bound and free of control characters.
    /// </summary>
    public static bool IsValidName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxNameLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a defined <see cref="WorkspaceStatus"/>. Used to
    /// reject undefined enum values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidStatus(WorkspaceStatus status) => Enum.IsDefined(status);

    /// <summary>
    /// Canonicalizes a slug to its stored form (trimmed and lower-cased using
    /// the invariant culture) and validates it. Canonicalization is purely
    /// casing/whitespace: it never rewrites characters, so an input that is not
    /// already a valid slug shape is rejected rather than coerced.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not a valid slug after canonicalization.
    /// </exception>
    public static string CanonicalizeSlug(string? value)
    {
        var canonical = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidSlug(canonical))
        {
            throw new ArgumentException("Slug violates the slug invariants.", nameof(value));
        }

        return canonical;
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs:
    /// workspace id, organization id and slug. The display name is excluded so
    /// it cannot leak through logs (threat T7: logs carry identifiers, not
    /// free-form metadata content).
    /// </summary>
    public override string ToString() => $"Workspace {Id} org={OrganizationId} slug={Slug} status={Status}";
}
