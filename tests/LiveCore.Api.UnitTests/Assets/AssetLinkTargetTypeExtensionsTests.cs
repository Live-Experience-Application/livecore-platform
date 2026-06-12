using LiveCore.Api.Assets;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="AssetLinkTargetTypeExtensions.ToVisibilityResourceType"/> (CORE-AST-005). The
/// mapping is what lets an asset's audience access be decided by the SAME central Visibility engine that
/// governs every other resource (visibility logic is never duplicated; docs/05_MODULE_CONTRACTS.md), so it
/// must be total over the defined members and fail-closed on an undefined value.
/// </summary>
public sealed class AssetLinkTargetTypeExtensionsTests
{
    [Fact]
    public void ContentBlock_maps_to_the_content_block_visibility_resource_type()
        => Assert.Equal(
            VisibilityResourceType.ContentBlock,
            AssetLinkTargetType.ContentBlock.ToVisibilityResourceType());

    [Fact]
    public void Entity_maps_to_the_entity_visibility_resource_type()
        => Assert.Equal(
            VisibilityResourceType.Entity,
            AssetLinkTargetType.Entity.ToVisibilityResourceType());

    [Fact]
    public void An_undefined_target_type_is_rejected_fail_closed()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => ((AssetLinkTargetType)999).ToVisibilityResourceType());
}
