namespace LiveCore.Api.Scenes;

/// <summary>
/// Outcome of the scene reorder command (CORE-SCENE-006, the "Authoring Completeness" epic), returned by
/// <see cref="SceneReorderService.ReorderAsync"/>.
///
/// The reorder is addressed by a workspace-scoped scene id, so the only two outcomes are that the scene
/// existed in the resolved tenant and workspace and was moved to its new position — re-packing the
/// workspace's scene ordering to a contiguous, gap-free, duplicate-free sequence (<see cref="Reordered"/>) —
/// or that no such scene exists there (<see cref="NotFound"/>). The endpoint maps <see cref="NotFound"/> to a
/// safe hidden-404 (an unknown id, or an id belonging to another workspace/tenant, reveals nothing and
/// changes nothing; threats T1/T5) and <see cref="Reordered"/> to <c>200 OK</c> with the re-packed scene
/// list. There is deliberately no "blocked" outcome: a reorder only moves a position and never fails on a
/// dependent. A concurrent reorder that would corrupt the order is not a result value — it surfaces as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> (the <c>xmin</c> token,
/// CORE-CONC-006) the endpoint pipeline translates to a <c>409 Conflict</c>.
/// </summary>
public enum SceneReorderResult
{
    /// <summary>
    /// The scene existed within the resolved tenant and workspace and was moved to its target position; the
    /// workspace's scenes re-packed to a contiguous, gap-free <c>scene_order</c> with no duplicates, their
    /// relative order otherwise preserved.
    /// </summary>
    Reordered = 1,

    /// <summary>
    /// No scene with the given id exists within the resolved tenant and workspace (an unknown id, or an id
    /// belonging to another workspace/tenant). Nothing was changed; the endpoint hides this as a safe 404
    /// (threats T1/T5).
    /// </summary>
    NotFound = 2,
}
