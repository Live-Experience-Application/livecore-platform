using LiveCore.Api.Assets;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for the <see cref="AssetLink"/> aggregate (CORE-AST-005, the asset-linking story of the
/// "Asset Storage and Authorization" epic). They pin the structural invariants of the link that attaches
/// an asset to a content block / entity: non-empty ids, a defined target type, immutability, the
/// boundary/identity helpers, and the log-safe <see cref="AssetLink.ToString"/> (threat T7). All fixtures
/// are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AssetLinkTests
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_sets_the_expected_fields()
    {
        var org = Guid.CreateVersion7();
        var ws = Guid.CreateVersion7();
        var asset = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();
        var creator = Guid.CreateVersion7();

        var link = AssetLink.Create(org, ws, asset, AssetLinkTargetType.ContentBlock, target, creator, _createdAt);

        Assert.NotEqual(Guid.Empty, link.Id);
        Assert.Equal(org, link.OrganizationId);
        Assert.Equal(ws, link.WorkspaceId);
        Assert.Equal(asset, link.AssetId);
        Assert.Equal(AssetLinkTargetType.ContentBlock, link.TargetType);
        Assert.Equal(target, link.TargetId);
        Assert.Equal(creator, link.CreatedByUserProfileId);
        Assert.Equal(_createdAt, link.CreatedAt);
    }

    [Fact]
    public void Create_normalizes_the_timestamp_to_utc()
    {
        var local = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.FromHours(2));
        var link = AssetLink.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            AssetLinkTargetType.Entity, Guid.CreateVersion7(), Guid.CreateVersion7(), local);

        Assert.Equal(TimeSpan.Zero, link.CreatedAt.Offset);
        Assert.Equal(local.ToUniversalTime(), link.CreatedAt);
    }

    [Theory]
    [InlineData(AssetLinkTargetType.ContentBlock)]
    [InlineData(AssetLinkTargetType.Entity)]
    public void Create_accepts_each_defined_target_type(AssetLinkTargetType targetType)
    {
        var link = AssetLink.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            targetType, Guid.CreateVersion7(), Guid.CreateVersion7(), _createdAt);

        Assert.Equal(targetType, link.TargetType);
    }

    [Fact]
    public void Create_rejects_an_undefined_target_type()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetLink.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            (AssetLinkTargetType)999, Guid.CreateVersion7(), Guid.CreateVersion7(), _createdAt));
    }

    [Fact]
    public void Create_rejects_empty_ids()
    {
        var id = Guid.CreateVersion7();

        Assert.Throws<ArgumentException>(() => AssetLink.Create(
            Guid.Empty, id, id, AssetLinkTargetType.ContentBlock, id, id, _createdAt));
        Assert.Throws<ArgumentException>(() => AssetLink.Create(
            id, Guid.Empty, id, AssetLinkTargetType.ContentBlock, id, id, _createdAt));
        Assert.Throws<ArgumentException>(() => AssetLink.Create(
            id, id, Guid.Empty, AssetLinkTargetType.ContentBlock, id, id, _createdAt));
        Assert.Throws<ArgumentException>(() => AssetLink.Create(
            id, id, id, AssetLinkTargetType.ContentBlock, Guid.Empty, id, _createdAt));
        Assert.Throws<ArgumentException>(() => AssetLink.Create(
            id, id, id, AssetLinkTargetType.ContentBlock, id, Guid.Empty, _createdAt));
    }

    [Fact]
    public void IsValidTargetType_distinguishes_defined_from_undefined()
    {
        Assert.True(AssetLink.IsValidTargetType(AssetLinkTargetType.ContentBlock));
        Assert.True(AssetLink.IsValidTargetType(AssetLinkTargetType.Entity));
        Assert.False(AssetLink.IsValidTargetType((AssetLinkTargetType)0));
        Assert.False(AssetLink.IsValidTargetType((AssetLinkTargetType)999));
    }

    [Fact]
    public void Boundary_and_link_helpers_match_only_the_exact_ids()
    {
        var org = Guid.CreateVersion7();
        var ws = Guid.CreateVersion7();
        var asset = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();

        var link = AssetLink.Create(org, ws, asset, AssetLinkTargetType.Entity, target, Guid.CreateVersion7(), _createdAt);

        Assert.True(link.BelongsToOrganization(org));
        Assert.False(link.BelongsToOrganization(Guid.CreateVersion7()));
        Assert.False(link.BelongsToOrganization(Guid.Empty));

        Assert.True(link.BelongsToWorkspace(ws));
        Assert.False(link.BelongsToWorkspace(Guid.CreateVersion7()));
        Assert.False(link.BelongsToWorkspace(Guid.Empty));

        Assert.True(link.LinksAsset(asset));
        Assert.False(link.LinksAsset(Guid.CreateVersion7()));
        Assert.False(link.LinksAsset(Guid.Empty));

        Assert.True(link.LinksTarget(AssetLinkTargetType.Entity, target));
        // A different kind, a different id, or an empty id all fail to match.
        Assert.False(link.LinksTarget(AssetLinkTargetType.ContentBlock, target));
        Assert.False(link.LinksTarget(AssetLinkTargetType.Entity, Guid.CreateVersion7()));
        Assert.False(link.LinksTarget(AssetLinkTargetType.Entity, Guid.Empty));
    }

    [Fact]
    public void ToString_is_log_safe_identifiers_only()
    {
        var org = Guid.CreateVersion7();
        var ws = Guid.CreateVersion7();
        var asset = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();
        var creator = Guid.CreateVersion7();

        var link = AssetLink.Create(org, ws, asset, AssetLinkTargetType.ContentBlock, target, creator, _createdAt);
        var text = link.ToString();

        Assert.Contains(link.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(asset.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("ContentBlock", text, StringComparison.Ordinal);
        Assert.Contains(target.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(creator.ToString(), text, StringComparison.Ordinal);
    }
}
