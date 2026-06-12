using LiveCore.Api.Visibility;

namespace LiveCore.Api.Assets;

/// <summary>
/// Maps an <see cref="AssetLinkTargetType"/> (the kinds an asset may be linked to, CORE-AST-005) onto the
/// central Visibility module's <see cref="VisibilityResourceType"/>, so an asset's audience access is
/// decided by the SAME visibility engine that governs every other resource — visibility logic is never
/// duplicated here (docs/05_MODULE_CONTRACTS.md: the Visibility module is "the central security module";
/// docs/02_ARCHITECTURE.md: "entity visibility is not computed ad hoc in many places"). The mapping is
/// total over the defined members and rejects any undefined value, fail-closed (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
internal static class AssetLinkTargetTypeExtensions
{
    /// <summary>
    /// Returns the <see cref="VisibilityResourceType"/> that governs the audience visibility of the given
    /// asset-link target kind: a <see cref="AssetLinkTargetType.ContentBlock"/> maps to
    /// <see cref="VisibilityResourceType.ContentBlock"/> and an <see cref="AssetLinkTargetType.Entity"/>
    /// maps to <see cref="VisibilityResourceType.Entity"/>. A <see cref="VisibilityResourceType.Scene"/>
    /// is never produced — a scene is not an asset-link target.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target type is not a defined member.</exception>
    public static VisibilityResourceType ToVisibilityResourceType(this AssetLinkTargetType targetType)
        => targetType switch
        {
            AssetLinkTargetType.ContentBlock => VisibilityResourceType.ContentBlock,
            AssetLinkTargetType.Entity => VisibilityResourceType.Entity,
            _ => throw new ArgumentOutOfRangeException(
                nameof(targetType),
                targetType,
                "Asset link target type is not a defined target type."),
        };
}
