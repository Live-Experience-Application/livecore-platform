using LiveCore.Api.Entitlements;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Unit tests for the <see cref="EntitlementDefinition"/> invariants and its <see cref="EntitlementDefinition.Define"/>
/// factory (CORE-ENTL-001, the plan and entitlement definition model story of the "Entitlements and Quotas"
/// epic). An entitlement definition is the generic, product-neutral catalog entry for one server-enforced
/// usage limit or premium capability (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md).
///
/// The epic acceptance is that "Generic entitlements can be defined without vertical terminology": the fixtures
/// use only generic Core keys (workspace.active.max, ads.disabled, ...) and generic display text, never a
/// vertical term (AGENTS.md, csv/forbidden_core_terms.csv). The tests pin the fail-closed invariants — the
/// generic dotted key shape, the bounded display/description, the immutable key and value kind, and the
/// soft-lifecycle flag — so a malformed or vertical-shaped definition can never be constructed.
/// </summary>
public class EntitlementDefinitionTests
{
    private const string _key = "workspace.active.max";
    private const string _displayName = "Active workspace limit";
    private const string _description = "Maximum number of concurrently active workspaces a subject may have.";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private static EntitlementDefinition QuotaDefinition()
        => EntitlementDefinition.Define(_key, EntitlementValueKind.Quota, _displayName, _description, _createdAt);

    // --- Factory: Define --------------------------------------------------------

    [Fact]
    public void Define_builds_an_active_definition()
    {
        var definition = QuotaDefinition();

        Assert.NotEqual(Guid.Empty, definition.Id);
        Assert.Equal(7, definition.Id.Version); // UUIDv7, time-ordered (docs/10_DATABASE_SCHEMA.md)
        Assert.Equal(_key, definition.Key);
        Assert.Equal(EntitlementValueKind.Quota, definition.ValueKind);
        Assert.Equal(_displayName, definition.DisplayName);
        Assert.Equal(_description, definition.Description);
        Assert.True(definition.IsActive);
        Assert.Equal(_createdAt, definition.CreatedAt);
        Assert.Equal(_createdAt, definition.UpdatedAt);
    }

    [Fact]
    public void Define_canonicalizes_the_key_to_lower_case_and_trims()
    {
        var definition = EntitlementDefinition.Define(
            "  Workspace.Active.Max  ", EntitlementValueKind.Quota, _displayName, null, _createdAt);

        Assert.Equal("workspace.active.max", definition.Key);
    }

    [Fact]
    public void Define_allows_a_flag_entitlement_with_no_description()
    {
        var definition = EntitlementDefinition.Define(
            "ads.disabled", EntitlementValueKind.Flag, "Ad-free experience", null, _createdAt);

        Assert.Equal(EntitlementValueKind.Flag, definition.ValueKind);
        Assert.Null(definition.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Workspace Active Max")] // spaces are not a valid dotted key
    [InlineData("workspace..active")] // doubled dot
    [InlineData(".workspace")] // leading dot
    [InlineData("workspace.")] // trailing dot
    [InlineData("workspace/active")] // disallowed character
    public void Define_rejects_an_invalid_key(string? key)
        => Assert.Throws<ArgumentException>(
            () => EntitlementDefinition.Define(key!, EntitlementValueKind.Quota, _displayName, null, _createdAt));

    [Fact]
    public void Define_rejects_an_oversize_key()
    {
        var tooLong = "a" + new string('b', EntitlementDefinition.MaxKeyLength);

        Assert.Throws<ArgumentException>(
            () => EntitlementDefinition.Define(tooLong, EntitlementValueKind.Quota, _displayName, null, _createdAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Define_rejects_a_blank_display_name(string? displayName)
        => Assert.Throws<ArgumentException>(
            () => EntitlementDefinition.Define(_key, EntitlementValueKind.Quota, displayName!, null, _createdAt));

    [Fact]
    public void Define_rejects_an_oversize_display_name()
    {
        var tooLong = new string('x', EntitlementDefinition.MaxDisplayNameLength + 1);

        Assert.Throws<ArgumentException>(
            () => EntitlementDefinition.Define(_key, EntitlementValueKind.Quota, tooLong, null, _createdAt));
    }

    [Fact]
    public void Define_rejects_an_oversize_description()
    {
        var tooLong = new string('x', EntitlementDefinition.MaxDescriptionLength + 1);

        Assert.Throws<ArgumentException>(
            () => EntitlementDefinition.Define(_key, EntitlementValueKind.Quota, _displayName, tooLong, _createdAt));
    }

    [Fact]
    public void Define_rejects_an_undefined_value_kind()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => EntitlementDefinition.Define(_key, (EntitlementValueKind)999, _displayName, null, _createdAt));

    // --- Soft lifecycle ---------------------------------------------------------

    [Fact]
    public void Deactivate_retires_the_definition_without_deleting_it()
    {
        var definition = QuotaDefinition();
        var updatedAt = _createdAt.AddHours(1);

        definition.Deactivate(updatedAt);

        Assert.False(definition.IsActive);
        Assert.Equal(updatedAt, definition.UpdatedAt);
        // The identity and value shape are unchanged: a retired definition still resolves for existing grants.
        Assert.Equal(_key, definition.Key);
        Assert.Equal(EntitlementValueKind.Quota, definition.ValueKind);
    }

    [Fact]
    public void Reactivate_brings_a_retired_definition_back()
    {
        var definition = QuotaDefinition();
        definition.Deactivate(_createdAt.AddHours(1));

        definition.Reactivate(_createdAt.AddHours(2));

        Assert.True(definition.IsActive);
    }

    [Fact]
    public void UpdateDetails_changes_display_metadata_but_not_the_key_or_kind()
    {
        var definition = QuotaDefinition();
        var updatedAt = _createdAt.AddHours(1);

        definition.UpdateDetails("Workspace cap", "New description.", updatedAt);

        Assert.Equal("Workspace cap", definition.DisplayName);
        Assert.Equal("New description.", definition.Description);
        Assert.Equal(updatedAt, definition.UpdatedAt);
        Assert.Equal(_key, definition.Key);
        Assert.Equal(EntitlementValueKind.Quota, definition.ValueKind);
    }

    [Fact]
    public void UpdateDetails_rejects_a_blank_display_name()
        => Assert.Throws<ArgumentException>(
            () => QuotaDefinition().UpdateDetails("   ", null, _createdAt.AddHours(1)));

    // --- UTC normalization ------------------------------------------------------

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        var definition = EntitlementDefinition.Define(
            _key, EntitlementValueKind.Quota, _displayName, null, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, definition.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), definition.CreatedAt);
    }

    // --- Object-level identity checks -------------------------------------------

    [Fact]
    public void HasKey_matches_only_the_exact_stored_key()
    {
        var definition = QuotaDefinition();

        Assert.True(definition.HasKey(_key));
        Assert.False(definition.HasKey("workspace.active.min"));
        Assert.False(definition.HasKey("WORKSPACE.ACTIVE.MAX")); // exact, case-sensitive
        Assert.False(definition.HasKey(""));
        Assert.False(definition.HasKey(null));
    }

    [Fact]
    public void HasId_matches_only_the_owning_id()
    {
        var definition = QuotaDefinition();

        Assert.True(definition.HasId(definition.Id));
        Assert.False(definition.HasId(Guid.CreateVersion7()));
        Assert.False(definition.HasId(Guid.Empty));
    }

    // --- Validation helpers -----------------------------------------------------

    [Theory]
    [InlineData("ads.disabled", true)]
    [InlineData("a", true)]
    [InlineData("session.participant.max", true)]
    [InlineData("UPPER", false)]
    [InlineData("with space", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidKey_enforces_the_generic_dotted_shape(string? value, bool expected)
        => Assert.Equal(expected, EntitlementDefinition.IsValidKey(value));

    // --- Log safety (threat T7) -------------------------------------------------

    [Fact]
    public void ToString_is_log_safe_and_excludes_the_display_metadata()
    {
        const string secretName = "INTERNAL-DISPLAY-NAME";
        const string secretDescription = "INTERNAL-DESCRIPTION";
        var definition = EntitlementDefinition.Define(
            _key, EntitlementValueKind.Flag, secretName, secretDescription, _createdAt);

        var rendered = definition.ToString();

        Assert.Contains(definition.Id.ToString(), rendered);
        Assert.Contains($"key={_key}", rendered);
        Assert.Contains("kind=Flag", rendered);
        Assert.DoesNotContain(secretName, rendered);
        Assert.DoesNotContain(secretDescription, rendered);
    }
}
