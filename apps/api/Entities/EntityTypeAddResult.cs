namespace LiveCore.Api.Entities;

/// <summary>
/// Outcome of persisting a new entity type (CORE-ENT-001).
///
/// Mirrors <c>WorkspaceAddResult</c>: an entity type has a per-workspace natural key
/// (<c>EntityType.TypeKey</c>), so an insert can be rejected as a DUPLICATE when the
/// workspace already defines a type with the same key. The (<c>workspace_id</c>,
/// <c>type_key</c>) pair is the per-workspace unique natural key enforced by a unique
/// database index, so a second writer can never overwrite an existing type with its own row.
/// </summary>
public enum EntityTypeAddResult
{
    /// <summary>The entity type was persisted.</summary>
    Added = 1,

    /// <summary>
    /// An entity type with the same key already exists in the same workspace. The existing
    /// entity type was not modified; the (<c>workspace_id</c>, <c>type_key</c>) pair is the
    /// per-workspace unique natural key, so a second writer can never overwrite an existing
    /// type with its own row (enforced by the unique database index). The same key is still
    /// available to other workspaces (threat T5).
    /// </summary>
    DuplicateKey = 2,
}
