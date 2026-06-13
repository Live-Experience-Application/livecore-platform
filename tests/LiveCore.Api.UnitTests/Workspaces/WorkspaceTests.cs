using LiveCore.Api.Workspaces;

namespace LiveCore.Api.UnitTests.Workspaces;

/// <summary>
/// Unit tests for the <see cref="Workspace"/> invariants (CORE-WS-001): the
/// workspace is the generic, tenant-scoped "container for a live experience"
/// addressed by an immutable surrogate id and an immutable, canonical slug that
/// is unique only WITHIN its organization; the display name is metadata only and
/// never a tenant boundary (threats T5/T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// ToString stays log-safe (threat T7). The aggregate carries
/// <c>organization_id</c> for the tenant boundary (docs/10_DATABASE_SCHEMA.md).
/// </summary>
public class WorkspaceTests
{
    private const string _slug = "summer-show";
    private const string _name = "Summer Show";

    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_copies_organization_slug_and_name()
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);

        Assert.NotEqual(Guid.Empty, workspace.Id);
        Assert.Equal(_organizationId, workspace.OrganizationId);
        Assert.Equal(_slug, workspace.Slug);
        Assert.Equal(_name, workspace.Name);
        Assert.Equal(_createdAt, workspace.CreatedAt);
        Assert.Equal(_createdAt, workspace.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = Workspace.Create(_organizationId, _slug, _name, _createdAt);
        var second = Workspace.Create(_organizationId, "winter-show", _name, _createdAt);

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_canonicalizes_slug_to_lower_case_and_trims()
    {
        var workspace = Workspace.Create(_organizationId, "  Summer-Show  ", _name, _createdAt);

        Assert.Equal("summer-show", workspace.Slug);
    }

    [Fact]
    public void Create_trims_name()
    {
        var workspace = Workspace.Create(_organizationId, _slug, "  Summer Show  ", _createdAt);

        Assert.Equal("Summer Show", workspace.Name);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
    {
        // The organization id is the tenant boundary; an empty value would
        // produce a workspace that belongs to no tenant (threat T5).
        Assert.Throws<ArgumentException>(
            () => Workspace.Create(Guid.Empty, _slug, _name, _createdAt));
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.FromHours(2));

        var workspace = Workspace.Create(_organizationId, _slug, _name, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, workspace.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), workspace.CreatedAt);
    }

    [Theory]
    [InlineData("a")] // shorter than the minimum length
    [InlineData("-leading")] // leading dash
    [InlineData("trailing-")] // trailing dash
    [InlineData("double--dash")] // doubled dash
    [InlineData("under_score")] // disallowed character
    [InlineData("white space")] // whitespace inside
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_invalid_slugs(string invalidSlug)
    {
        // Values here are invalid even after lower-casing/trimming. Casing-only
        // differences are canonicalized to a valid slug instead and are covered
        // by a separate test.
        Assert.Throws<ArgumentException>(
            () => Workspace.Create(_organizationId, invalidSlug, _name, _createdAt));
    }

    [Fact]
    public void Create_accepts_a_mixed_case_slug_by_canonicalizing_it()
    {
        var workspace = Workspace.Create(_organizationId, "Mixed-Case", _name, _createdAt);

        Assert.Equal("mixed-case", workspace.Slug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_names(string? blankName)
    {
        Assert.Throws<ArgumentException>(
            () => Workspace.Create(_organizationId, _slug, blankName!, _createdAt));
    }

    [Fact]
    public void Create_rejects_name_with_control_characters()
    {
        Assert.Throws<ArgumentException>(
            () => Workspace.Create(_organizationId, _slug, "Bad\tName", _createdAt));
    }

    [Fact]
    public void Create_rejects_name_over_the_length_limit()
    {
        var tooLong = new string('x', Workspace.MaxNameLength + 1);

        Assert.Throws<ArgumentException>(
            () => Workspace.Create(_organizationId, _slug, tooLong, _createdAt));
    }

    [Fact]
    public void Create_rejects_slug_over_the_length_limit()
    {
        var tooLong = new string('a', Workspace.MaxSlugLength + 1);

        Assert.Throws<ArgumentException>(
            () => Workspace.Create(_organizationId, tooLong, _name, _createdAt));
    }

    [Fact]
    public void Workspace_belongs_only_to_its_own_organization()
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);

        Assert.True(workspace.BelongsToOrganization(_organizationId));
        Assert.False(workspace.BelongsToOrganization(Guid.CreateVersion7()));
        Assert.False(workspace.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_both_the_organization_and_the_id_to_match()
    {
        // The surrogate id is only honored inside its tenant: a matching id under
        // a foreign organization must not identify the workspace (threat T1/T5).
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);
        var foreignOrganizationId = Guid.CreateVersion7();

        Assert.True(workspace.Identifies(_organizationId, workspace.Id));
        Assert.False(workspace.Identifies(foreignOrganizationId, workspace.Id));
        Assert.False(workspace.Identifies(_organizationId, Guid.CreateVersion7()));
        Assert.False(workspace.Identifies(_organizationId, Guid.Empty));
        Assert.False(workspace.Identifies(Guid.Empty, workspace.Id));
    }

    [Fact]
    public void HasSlugIn_requires_the_organization_and_an_exact_slug()
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);
        var foreignOrganizationId = Guid.CreateVersion7();

        Assert.True(workspace.HasSlugIn(_organizationId, _slug));
        // The same slug under a foreign organization is a different workspace.
        Assert.False(workspace.HasSlugIn(foreignOrganizationId, _slug));
        Assert.False(workspace.HasSlugIn(_organizationId, "winter-show"));
    }

    [Fact]
    public void HasSlugIn_is_case_sensitive_against_the_stored_canonical_value()
    {
        // The stored slug is canonical (lower-case); an upper-cased argument is a
        // different string and must never address the workspace (threat T5).
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);

        Assert.False(workspace.HasSlugIn(_organizationId, _slug.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasSlugIn_returns_false_for_blank_values(string? blankSlug)
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);

        Assert.False(workspace.HasSlugIn(_organizationId, blankSlug));
    }

    [Fact]
    public void Rename_changes_the_name_and_updated_at_but_not_the_organization_slug_or_id()
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);
        var originalId = workspace.Id;

        workspace.Rename("Summer Showcase", _updatedAt);

        Assert.Equal("Summer Showcase", workspace.Name);
        Assert.Equal(_updatedAt, workspace.UpdatedAt);
        Assert.Equal(_createdAt, workspace.CreatedAt);
        Assert.Equal(_slug, workspace.Slug);
        Assert.Equal(originalId, workspace.Id);
        Assert.Equal(_organizationId, workspace.OrganizationId);
    }

    [Fact]
    public void Rename_rejects_an_invalid_name_and_leaves_the_workspace_untouched()
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);

        Assert.Throws<ArgumentException>(() => workspace.Rename("   ", _updatedAt));

        Assert.Equal(_name, workspace.Name);
        Assert.Equal(_createdAt, workspace.UpdatedAt);
    }

    [Fact]
    public void Create_starts_active()
    {
        // A new workspace is in active use: it accepts authoring mutations and
        // appears in the active list (CORE-LIFE-009).
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);

        Assert.Equal(WorkspaceStatus.Active, workspace.Status);
        Assert.False(workspace.IsArchived);
        Assert.True(workspace.CanArchive);
    }

    [Fact]
    public void Archive_transitions_active_to_archived_and_stamps_updated_at()
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);
        var originalId = workspace.Id;

        workspace.Archive(_updatedAt);

        Assert.Equal(WorkspaceStatus.Archived, workspace.Status);
        Assert.True(workspace.IsArchived);
        Assert.False(workspace.CanArchive);
        Assert.Equal(_updatedAt, workspace.UpdatedAt);
        // Archiving is a soft, in-place transition: it never moves the workspace
        // (organization, slug and id are immutable; threat T5).
        Assert.Equal(_createdAt, workspace.CreatedAt);
        Assert.Equal(_slug, workspace.Slug);
        Assert.Equal(originalId, workspace.Id);
        Assert.Equal(_organizationId, workspace.OrganizationId);
    }

    [Fact]
    public void Archive_normalizes_updated_at_to_utc()
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);
        var localUpdatedAt = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.FromHours(2));

        workspace.Archive(localUpdatedAt);

        Assert.Equal(TimeSpan.Zero, workspace.UpdatedAt.Offset);
        Assert.Equal(localUpdatedAt.ToUniversalTime(), workspace.UpdatedAt);
    }

    [Fact]
    public void Archive_is_terminal_archiving_an_archived_workspace_throws()
    {
        // Archive is the clearly-terminal end-state: a second archive is an invalid
        // transition, not a no-op, so the application layer can surface a 409
        // instead of writing a duplicate audit fact (CORE-LIFE-009).
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);
        workspace.Archive(_updatedAt);

        Assert.Throws<InvalidOperationException>(() => workspace.Archive(_updatedAt));
        // The status and timestamp are unchanged by the rejected second archive.
        Assert.Equal(WorkspaceStatus.Archived, workspace.Status);
        Assert.Equal(_updatedAt, workspace.UpdatedAt);
    }

    [Fact]
    public void ToString_includes_the_lifecycle_status()
    {
        var workspace = Workspace.Create(_organizationId, _slug, _name, _createdAt);
        workspace.Archive(_updatedAt);

        Assert.Contains(WorkspaceStatus.Archived.ToString(), workspace.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("summer-show")]
    [InlineData("a1-b2-c3")]
    [InlineData("0ws")]
    public void IsValidSlug_accepts_canonical_slugs(string slug)
        => Assert.True(Workspace.IsValidSlug(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("UPPER")]
    [InlineData("-x")]
    [InlineData("x-")]
    [InlineData("a--b")]
    [InlineData("a b")]
    [InlineData("a_b")]
    public void IsValidSlug_rejects_non_canonical_slugs(string? slug)
        => Assert.False(Workspace.IsValidSlug(slug));

    [Fact]
    public void CanonicalizeSlug_lower_cases_and_trims_a_valid_value()
        => Assert.Equal("summer-show", Workspace.CanonicalizeSlug("  Summer-Show "));

    [Fact]
    public void CanonicalizeSlug_rejects_a_value_that_is_not_a_slug_even_after_casing()
        => Assert.Throws<ArgumentException>(() => Workspace.CanonicalizeSlug("not a slug"));

    [Fact]
    public void ToString_exposes_identifiers_but_not_the_display_name()
    {
        // Log-safety (threat T7): structured logs may carry identifiers (id,
        // organization id, slug), but never the free-form display name.
        var workspace = Workspace.Create(_organizationId, _slug, "Secret Workspace Name", _createdAt);

        var text = workspace.ToString();

        Assert.Contains(_slug, text, StringComparison.Ordinal);
        Assert.Contains(workspace.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Workspace Name", text, StringComparison.OrdinalIgnoreCase);
    }
}
