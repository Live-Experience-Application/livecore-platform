namespace LiveCore.Api.Scenes;

/// <summary>
/// Request body for creating a scene (CORE-SCENE-003,
/// <c>POST /api/v1/workspaces/{workspaceId}/scenes</c>, csv/api_routes.csv "Create
/// scene", roles Host,CoHost,Owner,Admin).
///
/// The target organization is supplied as <see cref="OrganizationSlug"/> and the
/// target workspace is taken from the route path, mirroring how the workspace
/// member-invite write resolves its tenant: the slug is matched against the caller's
/// token organization claim AND a persisted organization membership by the tenant
/// context resolver, and the create is then authorized by the caller's role in the
/// route's workspace (threat T5).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md): a scene
/// carries only a human-readable <see cref="Title"/>. It deliberately carries NO
/// order: the scene's position is assigned SERVER-SIDE as append-to-end (the next
/// order after the current maximum in the workspace), so a client can never choose,
/// skip or collide an ordering position (CORE-SCENE-001 deferred the append-to-end
/// ordering to this endpoint story). Reorder is a separate concern with no route in
/// csv/api_routes.csv and is out of scope here.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the target workspace, used to resolve
/// the tenant context (the route carries no organization in its path).
/// </param>
/// <param name="Title">Human-readable display title of the new scene.</param>
public sealed record CreateSceneRequest(string? OrganizationSlug, string? Title);

/// <summary>
/// Response projection of a scene (CORE-SCENE-003,
/// <c>GET /api/v1/workspaces/{workspaceId}/scenes</c> and
/// <c>POST /api/v1/workspaces/{workspaceId}/scenes</c>). It is the GENERIC,
/// product-neutral, server-side view of a scene returned to every workspace member.
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md,
/// docs/08_API_CONTRACTS.md DTO design rules): identifiers, the tenant and workspace
/// boundaries, the display title, the ordering position and the server timestamps
/// only. It carries:
/// <list type="bullet">
///   <item>NO host-only vs participant-only field separation — this story returns the
///   SAME generic scene DTO to all workspace members; the per-role / host-vs-participant
///   projection is the later CORE-SCENE-004 story (the "Projection by role" note in
///   csv/api_routes.csv is honored there, not here), exactly as the workspace read-one
///   route returns the same DTO to all members.</item>
///   <item>NO visibility fields and NO internal authorization rationale — it never
///   echoes why the caller was allowed or how the tenant/workspace was resolved
///   (docs/08; threat T7 in docs/07_SECURITY_THREAT_MODEL.md).</item>
/// </list>
///
/// Server timestamps are included per docs/08 ("Include server timestamps"). There is
/// deliberately NO resource version/ETag field: the <see cref="Scene"/> aggregate
/// carries no concurrency token, and these create/list operations have no concurrent
/// update to guard, so inventing one here would be speculative (docs/08 asks for a
/// version "where concurrent updates matter").
/// </summary>
/// <param name="Id">Surrogate id of the scene (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the scene belongs to.</param>
/// <param name="WorkspaceId">Workspace the scene belongs to.</param>
/// <param name="Title">Human-readable display title of the scene.</param>
/// <param name="Order">Ordering position of the scene within its workspace.</param>
/// <param name="CreatedAt">When the scene was created (UTC).</param>
/// <param name="UpdatedAt">When the scene was last updated (UTC).</param>
public sealed record SceneResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    string Title,
    int Order,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="Scene"/> aggregate into its response DTO. Only the
    /// generic, non-sensitive fields are copied.
    /// </summary>
    public static SceneResponse From(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        return new SceneResponse(
            scene.Id,
            scene.OrganizationId,
            scene.WorkspaceId,
            scene.Title,
            scene.Order,
            scene.CreatedAt,
            scene.UpdatedAt);
    }
}
