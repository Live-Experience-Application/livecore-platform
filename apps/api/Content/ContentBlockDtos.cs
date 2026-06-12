namespace LiveCore.Api.Content;

/// <summary>
/// Request body for creating a content block (CORE-SCENE-003,
/// <c>POST /api/v1/scenes/{sceneId}/content-blocks</c>, csv/api_routes.csv "Create
/// content block", roles Host,CoHost,Owner,Admin).
///
/// The target scene is taken from the route path, and the target organization is
/// supplied as a required <c>organizationSlug</c> QUERY parameter (the route path
/// carries no organization), so this DTO carries NEITHER: the tenant comes from the
/// query, the scene from the path. The scene is resolved within the query-supplied
/// organization, its own workspace id is discovered from the loaded row AFTER the
/// tenant boundary has been enforced, and the create is authorized by the caller's
/// role in the SCENE'S own workspace (threat T5).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md): a content
/// block carries only its generic kind (<see cref="Type"/> — Text/Media/Data) and the
/// content payload (<see cref="Body"/>). The block is created at the initial revision
/// (1) server-side; the client never supplies a revision number. It carries NO
/// visibility fields: whether a participant may see the block is computed server-side
/// by the Visibility module in a later epic, never named here
/// (docs/05_MODULE_CONTRACTS.md: the Content module "may not decide visibility alone").
/// </summary>
/// <param name="Type">
/// Generic kind of the block — the stable <see cref="ContentBlockType"/> name (Text,
/// Media or Data).
/// </param>
/// <param name="Body">The content payload of the block.</param>
public sealed record CreateContentBlockRequest(string? Type, string? Body);

/// <summary>
/// Response projection of a content block (CORE-SCENE-003,
/// <c>POST /api/v1/scenes/{sceneId}/content-blocks</c>). It is the GENERIC,
/// product-neutral, server-side view of the created content block.
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md,
/// docs/08_API_CONTRACTS.md DTO design rules): identifiers, the tenant, workspace and
/// scene boundaries, the generic type, the body, the revision number and the server
/// timestamps only. It carries:
/// <list type="bullet">
///   <item>NO host-only vs participant-only field separation — the host-vs-participant
///   DTO projection is the later CORE-SCENE-004 story; this story returns a single
///   generic DTO to the authorized writer.</item>
///   <item>NO visibility fields and NO internal authorization rationale (docs/08;
///   threat T7).</item>
/// </list>
///
/// The <see cref="Type"/> is emitted as the stable enum NAME (Text/Media/Data), never
/// the in-memory numeric discriminator, mirroring how the type is persisted and how the
/// session/workspace DTOs project their status/role. The <see cref="RevisionNumber"/>
/// is the monotonic version of the body and doubles as the concurrency token a later
/// optimistic-concurrency check can build on; server timestamps are included per
/// docs/08 ("Include server timestamps").
/// </summary>
/// <param name="Id">Surrogate id of the content block (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the content block belongs to.</param>
/// <param name="WorkspaceId">Workspace the content block belongs to.</param>
/// <param name="SceneId">Scene the content block belongs to.</param>
/// <param name="Type">Generic kind name of the block (Text/Media/Data).</param>
/// <param name="Body">The content payload of the current revision.</param>
/// <param name="RevisionNumber">Monotonic revision number (1 at creation).</param>
/// <param name="CreatedAt">When the content block was created (UTC).</param>
/// <param name="UpdatedAt">When the content block was last updated (UTC).</param>
public sealed record ContentBlockResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid SceneId,
    string Type,
    string Body,
    int RevisionNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="ContentBlock"/> aggregate into its response DTO. Only the
    /// generic fields are copied; the type is emitted as its stable name.
    /// </summary>
    public static ContentBlockResponse From(ContentBlock contentBlock)
    {
        ArgumentNullException.ThrowIfNull(contentBlock);

        return new ContentBlockResponse(
            contentBlock.Id,
            contentBlock.OrganizationId,
            contentBlock.WorkspaceId,
            contentBlock.SceneId,
            contentBlock.Type.ToString(),
            contentBlock.Body,
            contentBlock.RevisionNumber,
            contentBlock.CreatedAt,
            contentBlock.UpdatedAt);
    }
}
