using LiveCore.Api.Templates;

namespace LiveCore.Api.UnitTests.Templates;

/// <summary>
/// Unit tests for the <see cref="Template"/> aggregate invariants, its global/organization scope
/// (the nullable organization id), its canonical dot-separated template key, its version bound, its
/// definition validation, its scope guards (<see cref="Template.IsGlobal"/>,
/// <see cref="Template.BelongsToOrganization"/>, <see cref="Template.IsLoadableInto"/>,
/// <see cref="Template.Identifies"/>) and its log safety (CORE-ENT-004).
///
/// A template is the generic "Configurable vertical/domain template" (docs/03_DOMAIN_LANGUAGE.md);
/// it carries a versioned template definition and a global-or-organization scope; its ToString stays
/// log-safe and never exposes the definition content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): every example key/name/definition here is
/// GENERIC and NEUTRAL ("template-alpha", "sample.template", "type-alpha", "profile",
/// {"fields":[]}) — no vertical vocabulary appears anywhere, and the model treats the key and
/// definition purely as data (it never branches on their values), proving the template is generic
/// and data-driven (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class TemplateTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    // A well-formed, structurally valid definition: a templateKey plus a non-empty entityTypes
    // array of valid entries. All values are generic and neutral.
    private const string _validDefinition = """
        {
          "templateKey": "sample.template",
          "entityTypes": [
            { "key": "type-alpha", "displayName": "Type Alpha", "attributeSchema": { "fields": [] } }
          ]
        }
        """;

    // --- Creation invariants (global) ------------------------------------------

    [Fact]
    public void CreateGlobal_sets_the_metadata_and_is_global()
    {
        var template = Template.CreateGlobal("template-alpha", 1, _validDefinition, _createdAt);

        Assert.NotEqual(Guid.Empty, template.Id);
        Assert.Null(template.OrganizationId);
        Assert.True(template.IsGlobal);
        Assert.Equal("template-alpha", template.TemplateKey);
        Assert.Equal(1, template.Version);
        Assert.Equal(_validDefinition, template.Definition);
        Assert.Equal(_createdAt, template.CreatedAt);
        Assert.Equal(_createdAt, template.UpdatedAt);
    }

    [Fact]
    public void CreateForOrganization_sets_the_metadata_and_is_org_scoped()
    {
        var template = Template.CreateForOrganization(
            _organizationId, "template-alpha", 2, _validDefinition, _createdAt);

        Assert.Equal(_organizationId, template.OrganizationId);
        Assert.False(template.IsGlobal);
        Assert.Equal(2, template.Version);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = Template.CreateGlobal("template-alpha", 1, _validDefinition, _createdAt);
        var second = Template.CreateGlobal("template-beta", 1, _validDefinition, _createdAt);

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_canonicalizes_the_template_key_to_lower_case_and_trims()
    {
        // The key is a canonical dotted slug: casing and surrounding whitespace are normalized once
        // at creation so a re-cased key can never silently address a different template.
        var template = Template.CreateGlobal("  Sample.Template  ", 1, _validDefinition, _createdAt);

        Assert.Equal("sample.template", template.TemplateKey);
    }

    [Theory]
    [InlineData("sample.template")] // dotted, the docs example shape
    [InlineData("template-alpha")] // plain single slug segment
    [InlineData("a.b.c")] // several dotted segments
    [InlineData("ns.sub-part.v2")] // dashes within segments
    public void Create_accepts_valid_dotted_or_plain_template_keys(string key)
    {
        var template = Template.CreateGlobal(key, 1, _validDefinition, _createdAt);
        Assert.Equal(key, template.TemplateKey);
    }

    [Theory]
    [InlineData("")] // empty
    [InlineData("a")] // too short
    [InlineData(".leading-dot")] // leading dot
    [InlineData("trailing-dot.")] // trailing dot
    [InlineData("double..dot")] // doubled dot
    [InlineData("Has Space")] // space
    [InlineData("-leading-dash")] // leading dash
    [InlineData("under_score")] // underscore not allowed
    public void Create_rejects_a_malformed_template_key(string key)
    {
        Assert.Throws<ArgumentException>(
            () => Template.CreateGlobal(key, 1, _validDefinition, _createdAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_a_non_positive_version(int version)
    {
        Assert.Throws<ArgumentException>(
            () => Template.CreateGlobal("template-alpha", version, _validDefinition, _createdAt));
    }

    [Fact]
    public void CreateForOrganization_rejects_an_empty_organization_id()
    {
        Assert.Throws<ArgumentException>(
            () => Template.CreateForOrganization(Guid.Empty, "template-alpha", 1, _validDefinition, _createdAt));
    }

    [Fact]
    public void Create_rejects_a_malformed_or_structurally_invalid_definition()
    {
        // Not well-formed JSON.
        Assert.Throws<ArgumentException>(
            () => Template.CreateGlobal("template-alpha", 1, "{ not json", _createdAt));
        // Well-formed JSON but missing entityTypes.
        Assert.Throws<ArgumentException>(
            () => Template.CreateGlobal("template-alpha", 1, """{"templateKey":"sample.template"}""", _createdAt));
        // Well-formed JSON with an empty entityTypes array.
        Assert.Throws<ArgumentException>(
            () => Template.CreateGlobal(
                "template-alpha", 1, """{"templateKey":"sample.template","entityTypes":[]}""", _createdAt));
    }

    [Fact]
    public void Create_trims_the_definition()
    {
        var template = Template.CreateGlobal(
            "template-alpha", 1, "  " + _validDefinition + "  ", _createdAt);

        Assert.Equal(_validDefinition, template.Definition);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.FromHours(2));

        var template = Template.CreateGlobal("template-alpha", 1, _validDefinition, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, template.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), template.CreatedAt);
    }

    // --- Scope guards ----------------------------------------------------------

    [Fact]
    public void IsGlobal_distinguishes_global_from_org_scoped()
    {
        var global = Template.CreateGlobal("template-alpha", 1, _validDefinition, _createdAt);
        var orgScoped = Template.CreateForOrganization(_organizationId, "template-alpha", 1, _validDefinition, _createdAt);

        Assert.True(global.IsGlobal);
        Assert.False(orgScoped.IsGlobal);
    }

    [Fact]
    public void BelongsToOrganization_is_ownership_not_loadability()
    {
        var global = Template.CreateGlobal("template-alpha", 1, _validDefinition, _createdAt);
        var orgScoped = Template.CreateForOrganization(_organizationId, "template-alpha", 1, _validDefinition, _createdAt);

        // A global template is OWNED by no organization (even though it is loadable everywhere).
        Assert.False(global.BelongsToOrganization(_organizationId));
        // An org template belongs only to its own organization.
        Assert.True(orgScoped.BelongsToOrganization(_organizationId));
        Assert.False(orgScoped.BelongsToOrganization(_foreignOrganizationId));
        // Empty id matches nothing.
        Assert.False(orgScoped.BelongsToOrganization(Guid.Empty));
        Assert.False(global.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void IsLoadableInto_a_global_template_loads_into_any_organization()
    {
        var global = Template.CreateGlobal("template-alpha", 1, _validDefinition, _createdAt);

        Assert.True(global.IsLoadableInto(_organizationId));
        Assert.True(global.IsLoadableInto(_foreignOrganizationId));
        // An empty target id is never loadable.
        Assert.False(global.IsLoadableInto(Guid.Empty));
    }

    [Fact]
    public void IsLoadableInto_an_org_template_loads_only_into_its_own_organization()
    {
        // The TENANT SECURITY rule (threat T5): an org-A template is loadable into org A's
        // workspace but NEVER into org B's.
        var orgScoped = Template.CreateForOrganization(_organizationId, "template-alpha", 1, _validDefinition, _createdAt);

        Assert.True(orgScoped.IsLoadableInto(_organizationId));
        Assert.False(orgScoped.IsLoadableInto(_foreignOrganizationId));
        Assert.False(orgScoped.IsLoadableInto(Guid.Empty));
    }

    [Fact]
    public void Identifies_matches_only_within_the_same_scope()
    {
        var global = Template.CreateGlobal("template-alpha", 1, _validDefinition, _createdAt);
        var orgScoped = Template.CreateForOrganization(_organizationId, "template-alpha", 1, _validDefinition, _createdAt);

        // Global: matches a null scope + its own id.
        Assert.True(global.Identifies(null, global.Id));
        Assert.False(global.Identifies(_organizationId, global.Id)); // wrong scope
        Assert.False(global.Identifies(null, Guid.Empty)); // empty id
        Assert.False(global.Identifies(null, orgScoped.Id)); // wrong id

        // Org-scoped: matches its own organization + id only.
        Assert.True(orgScoped.Identifies(_organizationId, orgScoped.Id));
        Assert.False(orgScoped.Identifies(null, orgScoped.Id)); // wrong scope (global path)
        Assert.False(orgScoped.Identifies(_foreignOrganizationId, orgScoped.Id)); // wrong tenant
    }

    // --- Log safety (threat T7) ------------------------------------------------

    [Fact]
    public void ToString_is_log_safe_and_hides_the_definition()
    {
        var orgScoped = Template.CreateForOrganization(_organizationId, "template-alpha", 3, _validDefinition, _createdAt);
        var global = Template.CreateGlobal("template-beta", 1, _validDefinition, _createdAt);

        var orgText = orgScoped.ToString();
        var globalText = global.ToString();

        // Identifiers are present.
        Assert.Contains(orgScoped.Id.ToString(), orgText, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), orgText, StringComparison.Ordinal);
        Assert.Contains("template-alpha", orgText, StringComparison.Ordinal);
        Assert.Contains("v=3", orgText, StringComparison.Ordinal);
        // The global scope is shown as the literal "global", not an id.
        Assert.Contains("scope=global", globalText, StringComparison.Ordinal);

        // The definition content is NEVER exposed.
        Assert.DoesNotContain("entityTypes", orgText, StringComparison.Ordinal);
        Assert.DoesNotContain("attributeSchema", orgText, StringComparison.Ordinal);
        Assert.DoesNotContain("fields", orgText, StringComparison.Ordinal);
    }
}
