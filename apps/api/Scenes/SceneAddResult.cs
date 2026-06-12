namespace LiveCore.Api.Scenes;

/// <summary>
/// Outcome of persisting a new scene (CORE-SCENE-001).
///
/// A scene has no natural key — it is identified only by its surrogate id, and its
/// order column is deliberately non-unique (the ordering feature allows duplicate or
/// non-contiguous orders) — so there is no uniqueness constraint that an insert could
/// violate and therefore no "duplicate" outcome (unlike <c>ParticipantAddResult</c>,
/// where the filtered unique (workspace_id, user_id) index can reject a second
/// writer). This enum carries a single success value so the Add contract has the same
/// shape as the session and participant modules' and can grow a second outcome later
/// without a breaking signature change; foreign-key violations (a non-existent
/// workspace or tenant) surface as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository
/// rather than as a result value.
/// </summary>
public enum SceneAddResult
{
    /// <summary>The scene was persisted.</summary>
    Added = 1,
}
