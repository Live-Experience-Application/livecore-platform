namespace LiveCore.Api.Assets;

/// <summary>
/// Outcome of the asset-link removal command (CORE-LIFE-007, the "Resource Lifecycle and Deletion" epic),
/// returned by <see cref="AssetLinkService.UnlinkAsync"/>. It is the inverse of the create-link command
/// (<see cref="AssetLinkResult"/>).
///
/// The removal is addressed by an asset id AND a link id, both tenant- and workspace-scoped, so the only two
/// outcomes are that the link existed in the asset's own workspace AND attached the addressed asset and was
/// removed (<see cref="Removed"/>), or that no such link exists there (<see cref="NotFound"/>). The endpoint
/// maps <see cref="Removed"/> to <c>204 No Content</c> and <see cref="NotFound"/> to a safe hidden-404 — an
/// unknown link id, an id belonging to another workspace/tenant, or an id that resolves to a link of a
/// DIFFERENT asset all reveal nothing and change nothing (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// There is deliberately no "blocked" outcome: removing a link never refuses — the asset and the linked
/// target are unaffected either way — faithful to the add-link precedent (CORE-AST-005) and the
/// entity-relationship removal (CORE-LIFE-002), which likewise just succeed or report not-found.
/// </summary>
public enum AssetUnlinkResult
{
    /// <summary>
    /// The link existed within the asset's own organization and workspace and attached the addressed asset,
    /// and was hard-deleted. Only the link row was removed — the asset and the linked target are untouched.
    /// </summary>
    Removed = 1,

    /// <summary>
    /// No link with the given id exists within the asset's own organization and workspace attaching the
    /// addressed asset (an unknown id, an id belonging to another workspace/tenant, or an id that attaches a
    /// different asset). Nothing was changed; the endpoint hides this as a safe 404 (threats T1/T5).
    /// </summary>
    NotFound = 2,
}
