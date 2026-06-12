namespace LiveCore.Api.Assets;

/// <summary>
/// Outcome of persisting a new <see cref="AssetLink"/> (CORE-AST-005).
///
/// A link has a per-workspace natural key — the (workspace, asset, target type, target id) tuple — so an
/// insert can violate that uniqueness. This enum mirrors <c>EntityRelationshipAddResult</c>: a success
/// value and a duplicate value, so the same asset can never be linked to the same target twice (a repeat
/// is reported, not duplicated). Foreign-key violations (a non-existent asset, workspace, tenant or
/// creating user) surface as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the
/// repository rather than as a result value.
/// </summary>
public enum AssetLinkAddResult
{
    /// <summary>The link was persisted.</summary>
    Added = 1,

    /// <summary>
    /// The same asset is already linked to the same target (the unique
    /// (workspace_id, asset_id, target_type, target_id) index rejected the insert), so no second link was
    /// created.
    /// </summary>
    Duplicate = 2,
}
