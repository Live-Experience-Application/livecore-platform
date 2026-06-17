// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;

namespace LiveCore.Api.Scenes;

/// <summary>
/// The scene reorder command of the Scenes module (CORE-SCENE-006, the "Authoring Completeness" epic). A host
/// can MOVE a <see cref="Scene"/> to a new position within its workspace; this service re-packs the
/// workspace's scene ordering DETERMINISTICALLY so the result is contiguous (0..n-1) with no gaps and no
/// duplicates, inside ONE database transaction so a reorder is applied whole or not at all (the story's "the
/// workspace scene ordering re-packs deterministically with no gaps or duplicates" criterion).
///
/// SERVER-ASSIGNED ORDER, NOT CLIENT-TRUSTED (the story's "assign order server-side; no client-trusted
/// absolute order"). The caller supplies a desired 0-based TARGET INDEX — a list position, not an absolute
/// <c>scene_order</c> value. The service decides the actual orders: it lists the workspace's scenes in the
/// deterministic (scene_order, id) order (<see cref="ISceneRepository.ListByWorkspaceAsync"/>, the SCENE-001
/// ordering), removes the target scene from its current position, re-inserts it at the (clamped) target
/// index, and re-numbers every scene to its contiguous index position through <see cref="Scene.Reorder"/> —
/// the same aggregate ordering method the delete-repack uses. A client can therefore never choose, skip or
/// collide a position; it only expresses "put this scene at position N". The target index is clamped to the
/// valid range, so a too-large index simply moves the scene to the end (the endpoint rejects a NEGATIVE index
/// as a 400 before calling in).
///
/// REUSES THE DELETE-REPACK LOGIC (the story's "reuse the delete-repack logic"). Exactly like
/// <see cref="SceneDeletionService"/>'s re-pack, only the scenes whose order actually changes are persisted
/// (a no-op reorder writes nothing), and the relative order of the untouched scenes is preserved. A reorder
/// is a benign metadata change (a position move), not a security-relevant state transition or a deletion, so
/// — unlike the delete command — it appends NO audit record and emits NO event (the story names neither, and
/// there is no reorder action in the audit catalog; a rename is likewise un-audited).
///
/// SCOPE / ISOLATION (threats T1/T5). The service takes the already-resolved tenant and workspace and a scene
/// id, and loads the scene through the tenant- AND workspace-scoped
/// <see cref="ISceneRepository.FindByIdAsync(System.Guid, System.Guid, System.Guid, System.Threading.CancellationToken)"/>
/// FIRST: a scene in another workspace, or in a workspace owned by another tenant, is never found and so
/// never moved, even when its surrogate id is known. The list and every re-pack write are likewise tenant-
/// and workspace-scoped, so the re-pack can never reach across a workspace or tenant boundary. The endpoint
/// (<see cref="SceneEndpoints"/>) performs the authentication, tenant resolution and role authorization
/// before calling in; this service is the authorized command's effect.
///
/// ATOMICITY + CONCURRENCY (the story's "concurrent reorders do not corrupt the order"). The re-pack writes
/// run inside a single explicit transaction over the shared <see cref="LiveCoreDbContext"/> through
/// <see cref="TransactionalUnitOfWork"/>, so either every repositioned scene commits together or a failure
/// rolls the lot back, leaving the existing ordering intact. Each scene carries the <c>xmin</c> optimistic
/// concurrency token (CORE-CONC-006; <see cref="Scene.Reorder"/> is the read-modify-write that token guards),
/// so when two reorders interleave on the same workspace the SECOND writer's <c>UPDATE ... WHERE xmin =</c>
/// finds a row already advanced by the first and fails loudly with a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> (the endpoint pipeline's
/// <c>ConcurrencyConflictMiddleware</c> turns it into a <c>409 Conflict</c>) instead of silently overwriting
/// it — so two concurrent reorders converge on a valid permutation, never a corrupted one with duplicate or
/// gapped orders. The list is re-read INSIDE the transaction delegate, so a retry of the unit of work
/// (CORE-CONC-005, which clears the change tracker) re-runs against fresh database state.
/// </summary>
internal sealed class SceneReorderService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly ISceneRepository _scenes;

    public SceneReorderService(TransactionalUnitOfWork unitOfWork, ISceneRepository scenes)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(scenes);
        _unitOfWork = unitOfWork;
        _scenes = scenes;
    }

    /// <summary>
    /// Moves the scene identified by <paramref name="sceneId"/> within the given tenant and workspace to the
    /// position <paramref name="targetIndex"/> in the workspace's deterministic (scene_order, id) sequence,
    /// re-packing the remaining scenes to a contiguous, gap-free, duplicate-free ordering — all atomically.
    /// Returns <see cref="SceneReorderResult.Reordered"/> when the scene existed and was moved, or
    /// <see cref="SceneReorderResult.NotFound"/> when no scene with that id exists in the resolved tenant and
    /// workspace (an unknown id, or one belonging to another workspace/tenant — nothing is changed).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the scene belongs to.</param>
    /// <param name="sceneId">The surrogate id of the scene to move.</param>
    /// <param name="targetIndex">
    /// The desired 0-based position of the scene within its workspace's ordered scene list. It is CLAMPED to
    /// the valid range [0, count-1], so a value at or beyond the end moves the scene to the last position; it
    /// must be non-negative (the endpoint rejects a negative index as a 400 before calling in).
    /// </param>
    /// <param name="now">The command timestamp (the re-pack update timestamp of each moved scene).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The organization id, workspace id or scene id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The target index is negative.</exception>
    public async Task<SceneReorderResult> ReorderAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        int targetIndex,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sceneId == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(sceneId));
        }

        if (targetIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetIndex), targetIndex, "Target index must not be negative.");
        }

        // Load the scene WITHIN the resolved tenant AND workspace FIRST. FindByIdAsync leads with
        // organization_id then workspace_id then the scene id, so a scene in another workspace or tenant is
        // never returned even when the surrogate id matches; an unknown id is simply null. Reordering a
        // non-existent scene reveals nothing and changes nothing — a safe NotFound, and no transaction is
        // opened for it (threats T1/T5; mirrors SceneDeletionService).
        var scene = await _scenes
            .FindByIdAsync(organizationId, workspaceId, sceneId, cancellationToken)
            .ConfigureAwait(false);
        if (scene is null)
        {
            return SceneReorderResult.NotFound;
        }

        // ONE unit of work: the order re-pack commits together or rolls back together, so a reorder is applied
        // whole or not at all and can never leave a half-packed ordering. Every repository write goes through
        // the same scoped DbContext the transaction is begun on, so each SaveChanges enrols in this
        // transaction. The transaction is opened inside the EF execution strategy's ExecuteAsync, so it stays
        // correct under the retrying strategy (CORE-CONC-003/005); the list is therefore re-read INSIDE the
        // delegate so a retry runs against fresh state.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // The workspace's scenes in the deterministic (scene_order, id) order (the SCENE-001
                // ordering). Re-reading inside the transaction keeps a retried unit of work correct.
                var ordered = await _scenes
                    .ListByWorkspaceAsync(organizationId, workspaceId, transactionCancellationToken)
                    .ConfigureAwait(false);

                var currentIndex = IndexOf(ordered, sceneId);
                if (currentIndex < 0)
                {
                    // The scene was found above but is gone from the list now (a concurrent delete inside the
                    // race window). Nothing to move; a safe NotFound that changes nothing.
                    return SceneReorderResult.NotFound;
                }

                // The desired list: remove the scene from its current position and re-insert it at the target,
                // clamped to the valid range so a too-large index lands it at the end. This defines the new
                // relative order; the re-pack below turns it into contiguous scene_order values.
                var clampedTarget = Math.Clamp(targetIndex, 0, ordered.Count - 1);
                var moving = ordered[currentIndex];
                var desired = new List<Scene>(ordered);
                desired.RemoveAt(currentIndex);
                desired.Insert(clampedTarget, moving);

                // RE-PACK to a contiguous, gap-free, duplicate-free ordering: each scene's new order is its
                // index in the desired list. Only the scenes whose order actually changes are persisted (a
                // no-op reorder writes nothing and bumps no concurrency token), exactly as the delete-repack
                // does. Each UpdateAsync issues the xmin-guarded UPDATE, so a concurrent reorder is detected
                // (CORE-CONC-006).
                for (var index = 0; index < desired.Count; index++)
                {
                    var sibling = desired[index];
                    if (sibling.Order != index)
                    {
                        sibling.Reorder(index, now);
                        await _scenes.UpdateAsync(sibling, transactionCancellationToken).ConfigureAwait(false);
                    }
                }

                return SceneReorderResult.Reordered;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The index of the scene with the given id in the ordered list, or <c>-1</c> when it is absent. A scene
    /// is identified only by its surrogate id, which is unique within (and across) a workspace, so at most one
    /// element matches.
    /// </summary>
    private static int IndexOf(IReadOnlyList<Scene> scenes, Guid sceneId)
    {
        for (var index = 0; index < scenes.Count; index++)
        {
            if (scenes[index].Id == sceneId)
            {
                return index;
            }
        }

        return -1;
    }
}
