using LiveCore.Api.Content;

namespace LiveCore.Api.UnitTests.Content;

/// <summary>
/// Unit tests for the <see cref="ContentBlock"/> invariants, its tenant/workspace/scene
/// boundary and its revisions (CORE-SCENE-002): a content block is the generic
/// "Text/media/data unit shown or hidden by visibility rules"
/// (docs/03_DOMAIN_LANGUAGE.md); it is workspace-scoped and scene-scoped and belongs
/// only to the organization, workspace and scene it names; its
/// <see cref="ContentBlock.RevisionNumber"/> starts at 1 and
/// <see cref="ContentBlock.Revise"/> increments it while replacing the body; and
/// ToString stays log-safe (threat T7 in docs/07_SECURITY_THREAT_MODEL.md), never
/// leaking the host-prepared body. No vertical-specific terms appear anywhere —
/// generic ContentBlock vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class ContentBlockTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();
    private static readonly Guid _sceneId = Guid.CreateVersion7();
    private static readonly Guid _foreignSceneId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private const string _body = "The opening narration text.";

    private static ContentBlock CreateBlock(
        ContentBlockType type = ContentBlockType.Text,
        string body = _body)
        => ContentBlock.Create(_organizationId, _workspaceId, _sceneId, type, body, _createdAt);

    // --- Creation invariants ---------------------------------------------------

    [Fact]
    public void Create_sets_the_metadata_and_initial_revision()
    {
        var block = ContentBlock.Create(
            _organizationId, _workspaceId, _sceneId, ContentBlockType.Media, "asset://ref-1", _createdAt);

        Assert.NotEqual(Guid.Empty, block.Id);
        Assert.Equal(_organizationId, block.OrganizationId);
        Assert.Equal(_workspaceId, block.WorkspaceId);
        Assert.Equal(_sceneId, block.SceneId);
        Assert.Equal(ContentBlockType.Media, block.Type);
        Assert.Equal("asset://ref-1", block.Body);
        // The first version of the body is revision 1.
        Assert.Equal(ContentBlock.InitialRevisionNumber, block.RevisionNumber);
        Assert.Equal(1, block.RevisionNumber);
        Assert.Equal(_createdAt, block.CreatedAt);
        Assert.Equal(_createdAt, block.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = CreateBlock();
        var second = CreateBlock();

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_trims_the_body()
    {
        var block = ContentBlock.Create(
            _organizationId, _workspaceId, _sceneId, ContentBlockType.Text, "  Trimmed Body  ", _createdAt);

        Assert.Equal("Trimmed Body", block.Body);
    }

    [Theory]
    [InlineData(ContentBlockType.Text)]
    [InlineData(ContentBlockType.Media)]
    [InlineData(ContentBlockType.Data)]
    public void Create_accepts_every_defined_type(ContentBlockType type)
    {
        // Each type has its own valid body shape (CORE-SCENE-005): Text is plain text,
        // Media a reference string, Data a well-formed JSON document.
        var block = CreateBlock(type, ValidBodyFor(type));

        Assert.Equal(type, block.Type);
    }

    /// <summary>A minimal valid body for the given content type (per CORE-SCENE-005 rules).</summary>
    private static string ValidBodyFor(ContentBlockType type) => type switch
    {
        ContentBlockType.Text => "Some plain text.",
        ContentBlockType.Media => "asset://ref-1",
        ContentBlockType.Data => "{\"key\":\"value\"}",
        _ => "Some plain text.",
    };

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.FromHours(2));

        var block = ContentBlock.Create(
            _organizationId, _workspaceId, _sceneId, ContentBlockType.Text, _body, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, block.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), block.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(Guid.Empty, _workspaceId, _sceneId, ContentBlockType.Text, _body, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(_organizationId, Guid.Empty, _sceneId, ContentBlockType.Text, _body, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_scene_id()
        => Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(_organizationId, _workspaceId, Guid.Empty, ContentBlockType.Text, _body, _createdAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_body(string? body)
        => Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(_organizationId, _workspaceId, _sceneId, ContentBlockType.Text, body!, _createdAt));

    [Fact]
    public void Create_rejects_a_body_with_disallowed_control_characters()
        => Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(
                _organizationId, _workspaceId, _sceneId, ContentBlockType.Text, "bad\0body", _createdAt));

    [Fact]
    public void Create_allows_common_whitespace_in_the_body()
    {
        // Tab/CR/LF legitimately appear in textual content, so they are not rejected as
        // control characters. (Text body — a Data body now requires well-formed JSON,
        // CORE-SCENE-005, so this multi-line plain-text body is a Text block.)
        var block = ContentBlock.Create(
            _organizationId, _workspaceId, _sceneId, ContentBlockType.Text, "line one\nline two\tindented", _createdAt);

        Assert.Equal("line one\nline two\tindented", block.Body);
    }

    [Fact]
    public void Create_rejects_a_body_over_the_per_type_length_bound()
    {
        // Per-type size limit (CORE-SCENE-005): a Text body over MaxTextLength is rejected.
        var tooLong = new string('a', ContentValidator.MaxTextLength + 1);

        Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(_organizationId, _workspaceId, _sceneId, ContentBlockType.Text, tooLong, _createdAt));
    }

    [Fact]
    public void Create_rejects_an_undefined_type()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => ContentBlock.Create(
                _organizationId, _workspaceId, _sceneId, (ContentBlockType)999, _body, _createdAt));

    // --- Per-type content validation + size limits (CORE-SCENE-005) ------------
    // The aggregate enforces the SAME per-type rules the ContentValidator exposes, so a
    // body that is invalid for its type is rejected at Create AND Revise with no mutation.

    [Fact]
    public void Create_accepts_a_well_formed_json_data_body()
    {
        // Data: a structured, well-formed JSON document is accepted (well-formedness only —
        // the JSON schema is never validated here, that is the Templates epic).
        var block = ContentBlock.Create(
            _organizationId, _workspaceId, _sceneId, ContentBlockType.Data, "{\"key\":\"value\"}", _createdAt);

        Assert.Equal("{\"key\":\"value\"}", block.Body);
        Assert.Equal(ContentBlockType.Data, block.Type);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"unterminated\":")]
    [InlineData("{ key: 'value' }")] // unquoted key + single quotes are not valid JSON
    public void Create_rejects_a_malformed_json_data_body(string body)
        // Data: a body that is not well-formed JSON is rejected.
        => Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(_organizationId, _workspaceId, _sceneId, ContentBlockType.Data, body, _createdAt));

    [Fact]
    public void Create_accepts_a_media_reference_body()
    {
        // Media: a non-blank, bounded reference string is accepted (the reference is NOT
        // resolved against a real Asset — asset linkage is the later CORE-AST-005 story).
        var block = ContentBlock.Create(
            _organizationId, _workspaceId, _sceneId, ContentBlockType.Media, "asset://ref-1", _createdAt);

        Assert.Equal("asset://ref-1", block.Body);
        Assert.Equal(ContentBlockType.Media, block.Type);
    }

    [Theory]
    [InlineData(ContentBlockType.Text, "bad\0body")]
    [InlineData(ContentBlockType.Media, "bad\0ref")]
    public void Create_rejects_a_disallowed_control_character_for_text_and_media(ContentBlockType type, string body)
        => Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(_organizationId, _workspaceId, _sceneId, type, body, _createdAt));

    [Theory]
    [InlineData(ContentBlockType.Text)]
    [InlineData(ContentBlockType.Media)]
    public void Create_accepts_a_body_at_exactly_the_per_type_length_limit(ContentBlockType type)
    {
        // At-limit boundary (CORE-SCENE-005): a body of exactly the per-type maximum length
        // is accepted. Text and Media bodies are plain reference/text characters.
        var atLimit = new string('a', ContentValidator.MaxLengthFor(type));

        var block = ContentBlock.Create(_organizationId, _workspaceId, _sceneId, type, atLimit, _createdAt);

        Assert.Equal(atLimit.Length, block.Body.Length);
    }

    [Theory]
    [InlineData(ContentBlockType.Text)]
    [InlineData(ContentBlockType.Media)]
    public void Create_rejects_a_body_one_over_the_per_type_length_limit(ContentBlockType type)
    {
        // Over-limit boundary (CORE-SCENE-005): one character over the per-type maximum is
        // rejected.
        var overLimit = new string('a', ContentValidator.MaxLengthFor(type) + 1);

        Assert.Throws<ArgumentException>(
            () => ContentBlock.Create(_organizationId, _workspaceId, _sceneId, type, overLimit, _createdAt));
    }

    [Fact]
    public void Revise_rejects_a_malformed_json_data_body_and_leaves_the_block_untouched()
    {
        // Revise enforces the new type's rule too: replacing a Text body with a malformed
        // JSON Data body is rejected with NO mutation (prior revision intact).
        var block = CreateBlock(ContentBlockType.Text, "Original body");

        Assert.Throws<ArgumentException>(
            () => block.Revise(ContentBlockType.Data, "not json", _updatedAt));

        Assert.Equal(ContentBlockType.Text, block.Type);
        Assert.Equal("Original body", block.Body);
        Assert.Equal(1, block.RevisionNumber);
        Assert.Equal(_createdAt, block.UpdatedAt);
    }

    [Fact]
    public void Revise_accepts_a_well_formed_json_data_body()
    {
        var block = CreateBlock(ContentBlockType.Text, "Original body");

        block.Revise(ContentBlockType.Data, "{\"a\":1}", _updatedAt);

        Assert.Equal(ContentBlockType.Data, block.Type);
        Assert.Equal("{\"a\":1}", block.Body);
        Assert.Equal(2, block.RevisionNumber);
    }

    [Fact]
    public void IsValidRevisionNumber_accepts_one_and_above_and_rejects_below_one()
    {
        Assert.True(ContentBlock.IsValidRevisionNumber(1));
        Assert.True(ContentBlock.IsValidRevisionNumber(42));
        Assert.True(ContentBlock.IsValidRevisionNumber(int.MaxValue));
        Assert.False(ContentBlock.IsValidRevisionNumber(0));
        Assert.False(ContentBlock.IsValidRevisionNumber(-1));
        Assert.False(ContentBlock.IsValidRevisionNumber(int.MinValue));
    }

    [Fact]
    public void Constructing_with_an_updated_before_created_is_rejected()
    {
        // The aggregate enforces updatedAt >= createdAt: a content block cannot be
        // updated before it was created. The Create factory always passes createdAt ==
        // updatedAt, so the invariant is reached through the private materialization
        // constructor (the path a corrupt/hand-built row would take). The guard throws
        // an ArgumentException, surfaced through reflection as the inner exception.
        var constructor = typeof(ContentBlock).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid), typeof(ContentBlockType),
                typeof(string), typeof(int), typeof(DateTimeOffset), typeof(DateTimeOffset),
            },
            modifiers: null);
        Assert.NotNull(constructor);

        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            constructor.Invoke(new object[]
            {
                Guid.CreateVersion7(),
                _organizationId,
                _workspaceId,
                _sceneId,
                ContentBlockType.Text,
                _body,
                ContentBlock.InitialRevisionNumber,
                // createdAt is _updatedAt (later); updatedAt is _createdAt (earlier).
                _updatedAt,
                _createdAt,
            }));

        Assert.IsType<ArgumentException>(invocation.InnerException);
    }

    // --- Tenant / workspace / scene boundary -----------------------------------

    [Fact]
    public void Block_belongs_only_to_its_own_organization()
    {
        // Negative tenant case (threat T5): a content block belongs only to the
        // organization it names. The organization boundary is checked before the
        // workspace boundary.
        var block = CreateBlock();

        Assert.True(block.BelongsToOrganization(_organizationId));
        Assert.False(block.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(block.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void Block_belongs_only_to_its_own_workspace()
    {
        // Negative workspace case (threat T5): a block in workspace W1 is not in
        // workspace W2.
        var block = CreateBlock();

        Assert.True(block.BelongsToWorkspace(_workspaceId));
        Assert.False(block.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(block.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Block_belongs_only_to_its_own_scene()
    {
        // Negative scene case (threat T5): a block in scene S1 is not in scene S2.
        var block = CreateBlock();

        Assert.True(block.BelongsToScene(_sceneId));
        Assert.False(block.BelongsToScene(_foreignSceneId));
        Assert.False(block.BelongsToScene(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_exact_organization_workspace_and_id()
    {
        // A content block is the one identified by its id only WITHIN its tenant and
        // workspace: the surrogate id is never honored across a foreign tenant or
        // workspace (threat T1/T5).
        var block = CreateBlock();

        Assert.True(block.Identifies(_organizationId, _workspaceId, block.Id));
        Assert.False(block.Identifies(_foreignOrganizationId, _workspaceId, block.Id));
        Assert.False(block.Identifies(_organizationId, _foreignWorkspaceId, block.Id));
        Assert.False(block.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(block.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    // --- Revisions (the headline feature) --------------------------------------

    [Fact]
    public void Revise_increments_the_revision_number_and_replaces_the_body()
    {
        var block = CreateBlock(ContentBlockType.Text, "Original body");
        var blockId = block.Id;

        block.Revise(ContentBlockType.Text, "Revised body", _updatedAt);

        Assert.Equal("Revised body", block.Body);
        // Revision 1 -> 2: the counter increases by exactly one.
        Assert.Equal(2, block.RevisionNumber);
        Assert.Equal(_updatedAt, block.UpdatedAt);
        Assert.Equal(_createdAt, block.CreatedAt);
        // The tenant boundary, the workspace, the scene and the id are immutable across
        // a revision (threat T5): revising never moves the block.
        Assert.Equal(blockId, block.Id);
        Assert.Equal(_organizationId, block.OrganizationId);
        Assert.Equal(_workspaceId, block.WorkspaceId);
        Assert.Equal(_sceneId, block.SceneId);
    }

    [Fact]
    public void Revise_increments_monotonically_across_multiple_revisions()
    {
        var block = CreateBlock();

        Assert.Equal(1, block.RevisionNumber);

        block.Revise(ContentBlockType.Text, "rev 2", _updatedAt);
        Assert.Equal(2, block.RevisionNumber);

        block.Revise(ContentBlockType.Text, "rev 3", _updatedAt);
        Assert.Equal(3, block.RevisionNumber);

        block.Revise(ContentBlockType.Text, "rev 4", _updatedAt);
        Assert.Equal(4, block.RevisionNumber);
    }

    [Fact]
    public void Revise_can_change_the_content_type()
    {
        // A revision may replace the body with a different kind of content.
        var block = CreateBlock(ContentBlockType.Text, "some text");

        block.Revise(ContentBlockType.Media, "asset://ref-9", _updatedAt);

        Assert.Equal(ContentBlockType.Media, block.Type);
        Assert.Equal("asset://ref-9", block.Body);
        Assert.Equal(2, block.RevisionNumber);
    }

    [Fact]
    public void Revise_trims_the_new_body()
    {
        var block = CreateBlock();

        block.Revise(ContentBlockType.Text, "  Spaced  ", _updatedAt);

        Assert.Equal("Spaced", block.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Revise_rejects_a_blank_body_and_leaves_the_block_untouched(string? body)
    {
        // An invalid revision is rejected with NO mutation: the prior revision's body
        // and number are left intact (prior-revision immutability).
        var block = CreateBlock(ContentBlockType.Text, "Original body");

        Assert.Throws<ArgumentException>(() => block.Revise(ContentBlockType.Text, body!, _updatedAt));

        Assert.Equal("Original body", block.Body);
        Assert.Equal(1, block.RevisionNumber);
        Assert.Equal(_createdAt, block.UpdatedAt);
    }

    [Fact]
    public void Revise_rejects_an_undefined_type_and_leaves_the_block_untouched()
    {
        var block = CreateBlock(ContentBlockType.Text, "Original body");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => block.Revise((ContentBlockType)999, "Revised body", _updatedAt));

        // The rejected revision did not change the type, body, revision number or
        // re-stamp the update timestamp.
        Assert.Equal(ContentBlockType.Text, block.Type);
        Assert.Equal("Original body", block.Body);
        Assert.Equal(1, block.RevisionNumber);
        Assert.Equal(_createdAt, block.UpdatedAt);
    }

    [Fact]
    public void Revise_rejects_a_body_over_the_per_type_length_bound_and_leaves_the_block_untouched()
    {
        var block = CreateBlock(ContentBlockType.Text, "Original body");
        var tooLong = new string('a', ContentValidator.MaxTextLength + 1);

        Assert.Throws<ArgumentException>(() => block.Revise(ContentBlockType.Text, tooLong, _updatedAt));

        Assert.Equal("Original body", block.Body);
        Assert.Equal(1, block.RevisionNumber);
    }

    [Fact]
    public void Revise_normalizes_the_timestamp_to_utc()
    {
        var localUpdatedAt = new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.FromHours(2));
        var block = CreateBlock();

        block.Revise(ContentBlockType.Text, "Revised body", localUpdatedAt);

        Assert.Equal(TimeSpan.Zero, block.UpdatedAt.Offset);
        Assert.Equal(localUpdatedAt.ToUniversalTime(), block.UpdatedAt);
    }

    // --- Log safety ------------------------------------------------------------

    [Fact]
    public void ToString_exposes_identifiers_type_and_revision_but_no_body()
    {
        // Log-safety (threat T7): structured logs carry identifiers, the type and the
        // revision number, never the host-prepared body content.
        var block = CreateBlock(ContentBlockType.Text, "Secret narration text");

        var text = block.ToString();

        Assert.Contains(block.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_sceneId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("type=Text", text, StringComparison.Ordinal);
        Assert.Contains("rev=1", text, StringComparison.Ordinal);
        // The body is never written to logs.
        Assert.DoesNotContain("Secret narration text", text, StringComparison.Ordinal);
    }
}
