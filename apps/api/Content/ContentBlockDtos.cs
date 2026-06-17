using LiveCore.Api.Organizations;
using LiveCore.Api.Visibility;

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
/// HOST-facing response projection of a content block (CORE-SCENE-003 create,
/// <c>POST /api/v1/scenes/{sceneId}/content-blocks</c>; reused as the host shape by the CORE-CB-001 list and
/// by-id read routes). It is the FULL, product-neutral, server-side view of a content block returned to the
/// host-CONTENT workspace roles (Owner/Admin/Host/CoHost) — the create role is always a host-content role, and
/// on the read routes <see cref="ContentBlockProjection"/> selects this shape for the host-content roles and
/// the stripped <see cref="ParticipantContentBlockResponse"/> for every other role.
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md,
/// docs/08_API_CONTRACTS.md DTO design rules): identifiers, the tenant, workspace and
/// scene boundaries, the generic type, the body, the revision number and the server
/// timestamps only. It carries:
/// <list type="bullet">
///   <item>The host-only fields including the BODY content — this is the host half of the
///   host-vs-participant DTO separation (docs/08 "Host DTOs and Participant DTOs are different"); it is NEVER
///   returned to an audience (Participant/Observer) or audit (Auditor) caller, who receive
///   <see cref="ParticipantContentBlockResponse"/> instead.</item>
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

/// <summary>
/// PARTICIPANT-facing response projection of a content block (CORE-CB-001). It is the audience-safe,
/// host-only-field-STRIPPED half of the host-vs-participant DTO separation for the content-block list and
/// by-id read routes: the shape returned to the audience workspace roles (Participant/Observer) — and,
/// fail-closed, the audit role (Auditor) and any undefined role — instead of the full
/// <see cref="ContentBlockResponse"/>.
///
/// docs/08_API_CONTRACTS.md states "Host DTOs and Participant DTOs are different" and "Participant DTOs must
/// not contain hidden content fields", so it is a separate record type, NOT a reused/optional-field variant of
/// the host DTO. A content block IS content — its <see cref="ContentBlockResponse.Body"/> is the host-prepared
/// "Text/media/data unit shown or hidden by visibility rules" (docs/03_DOMAIN_LANGUAGE.md) — so the role split
/// is the "View host-only content" row of docs/06_AUTHORIZATION_MATRIX.md (the same as the entity projection,
/// NOT the scene-style "View workspace metadata" split). The audience-safe shape therefore carries only the
/// minimal, non-sensitive content-block IDENTITY and strips everything host-only, most importantly the
/// BODY (the unrevealed content; threat T2).
///
/// EXACT field set and the doc citation justifying each OMISSION:
/// <list type="bullet">
///   <item><c>Id</c> — KEPT: the surrogate id is a non-sensitive correlation handle; it never authorizes
///   access on its own (every lookup is tenant+workspace+scene scoped, threats T1/T5).</item>
///   <item><c>Type</c> — KEPT: the generic kind (Text/Media/Data) is a non-sensitive descriptor, the minimal
///   audience-relevant identity of the block (the analogue of the entity
///   <see cref="Entities.ParticipantEntityResponse"/> keeping the entity name). It is NOT content — the actual
///   payload is the body, which is stripped.</item>
///   <item><c>Body</c> — OMITTED: the content payload is host-only CONTENT that a participant must never
///   receive until a separate reveal makes it visible (docs/06 "View host-only content" = no for the audience
///   roles, audit-only for Auditor; docs/08 forbids hidden content fields in participant DTOs; threats
///   T2/T7). Whether an audience member may actually see a block's body is the central Visibility module's
///   session-scoped concern (the participant visible-feed projection), never exposed wholesale here.</item>
///   <item><c>OrganizationId</c> / <c>WorkspaceId</c> / <c>SceneId</c> — OMITTED: internal tenant, workspace
///   and scene boundary identifiers, stripped exactly as <see cref="Entities.ParticipantEntityResponse"/>
///   strips the tenant/workspace/type ids (docs/06 "limited"; docs/08 no hidden/internal fields).</item>
///   <item><c>RevisionNumber</c> — OMITTED: the body's monotonic version is host preparation metadata (it
///   reveals how many times the host edited the block), host-only metadata an audience member does not
///   receive.</item>
///   <item><c>CreatedAt</c> / <c>UpdatedAt</c> — OMITTED: host preparation timestamps, host-only metadata
///   (docs/08; the host DTO satisfies "Include server timestamps").</item>
/// </list>
///
/// It carries NO hidden content fields, NO host-only fields and NO internal authorization rationale (docs/08;
/// threats T2/T7). Adding any host-only field to this record would be caught by the integration tests that
/// assert its EXACT top-level JSON property set.
/// </summary>
/// <param name="Id">Surrogate id of the content block (UUIDv7); a non-sensitive handle.</param>
/// <param name="Type">Generic kind name of the block (Text/Media/Data); NOT the content payload.</param>
public sealed record ParticipantContentBlockResponse(
    Guid Id,
    string Type)
{
    /// <summary>
    /// Projects a <see cref="ContentBlock"/> aggregate into its audience-safe participant DTO. Only the
    /// minimal audience-relevant fields (id, type name) are copied; the body CONTENT, the boundary ids, the
    /// revision number and the host preparation timestamps are deliberately NOT copied (see the type summary).
    /// </summary>
    public static ParticipantContentBlockResponse From(ContentBlock contentBlock)
    {
        ArgumentNullException.ThrowIfNull(contentBlock);

        return new ParticipantContentBlockResponse(contentBlock.Id, contentBlock.Type.ToString());
    }
}

/// <summary>
/// Role-based content-block PROJECTOR (CORE-CB-001) — the pure mapping that honors the "Projection by role"
/// obligation on the content-block list and by-id read routes (csv/api_routes.csv). It selects WHICH of the
/// two distinct content-block DTO shapes (<see cref="ContentBlockResponse"/> host shape vs
/// <see cref="ParticipantContentBlockResponse"/> audience shape) a caller receives, from the caller's
/// WORKSPACE <see cref="MembershipRole"/>.
///
/// It changes only the SHAPE of each block, never the SET of blocks: every member of a workspace receives ALL
/// of the route's scene's content blocks (the list is allowed to "workspace members" per csv/api_routes.csv).
/// Deciding WHICH blocks a participant may actually see the BODY of (audience calculation, per-block reveal
/// "if visible" filtering) is the central Visibility module's job (docs/05_MODULE_CONTRACTS.md), realized
/// session-scoped by the participant visible-feed — NOT this projector. This type contains NO visibility-engine
/// logic; it fails closed by stripping the body from every block for a non-host role.
///
/// Role -> shape mapping. A content block IS content (its body is the host-prepared payload), so the split is
/// decided STRICTLY from the "View host-only content" row of docs/06_AUTHORIZATION_MATRIX.md (NOT "View
/// workspace metadata"). It DELEGATES to the central Visibility module's canonical classification
/// (<see cref="VisibilityRoles.ViewsHostOnlyContent"/>, CORE-VIS-002) so the host-content role set
/// (Owner/Admin/Host/CoHost) is defined in ONE place and never duplicated (docs/02_ARCHITECTURE.md;
/// docs/05_MODULE_CONTRACTS.md) — the same delegation <see cref="Entities.EntityProjection"/> uses.
/// <list type="bullet">
///   <item>Owner, Admin, Host, CoHost -> <see cref="ContentBlockResponse"/> (host-content roles; "View
///   host-only content" = yes).</item>
///   <item>Participant, Observer (audience), Auditor (audit-only) and any undefined value ->
///   <see cref="ParticipantContentBlockResponse"/>. The audience roles get the stripped shape because their
///   content access is "if visible" (computed session-scoped elsewhere); Auditor is EXCLUDED from the
///   host-content shape because the matrix grants it only <c>audit-only</c>, not <c>yes</c> ("Audit roles …
///   should not automatically view sensitive content unless explicitly allowed", docs/06) — the same
///   distinction <see cref="Entities.EntityProjection"/> draws, because a content block (like an entity) IS
///   content.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is NON-LINEAR (the enum's integer values are storage discriminators only and
/// must never be compared with &gt;/&lt;), so the classification is an EXACT set-membership check, never an
/// ordering comparison. Any value outside the host-content set fails closed to the stripped participant shape
/// (deny-by-default: an unrecognized role is never granted the host-content view; threats T1/T5).
/// </summary>
internal static class ContentBlockProjection
{
    /// <summary>
    /// Whether the given workspace role receives the full HOST content-block shape
    /// (<see cref="ContentBlockResponse"/>, including the body content) rather than the stripped
    /// <see cref="ParticipantContentBlockResponse"/>. DELEGATES to the central
    /// <see cref="VisibilityRoles.ViewsHostOnlyContent"/> (Owner/Admin/Host/CoHost); every other role
    /// (Participant/Observer, Auditor and any undefined value) is denied the host shape and falls closed to
    /// the participant shape. Never a &gt;/&lt; comparison.
    /// </summary>
    public static bool ReceivesHostShape(MembershipRole role)
        => VisibilityRoles.ViewsHostOnlyContent(role);

    /// <summary>
    /// Projects the given content blocks into the role-appropriate response array, boxed as
    /// <see cref="object"/> so the endpoint can return either the host (<see cref="ContentBlockResponse"/>) or
    /// the participant (<see cref="ParticipantContentBlockResponse"/>) array as the response body. The SET of
    /// blocks is unchanged — only the per-block SHAPE differs by role.
    /// </summary>
    public static object Project(IEnumerable<ContentBlock> contentBlocks, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(contentBlocks);

        return ReceivesHostShape(role)
            ? contentBlocks.Select(ContentBlockResponse.From).ToArray()
            : contentBlocks.Select(ParticipantContentBlockResponse.From).ToArray();
    }

    /// <summary>
    /// Projects ONE BOUNDED PAGE of content blocks into the role-appropriate
    /// <see cref="LiveCore.Api.PageResponse{T}"/> envelope (CORE-DX-003), boxed as <see cref="object"/> so the
    /// endpoint can return either the host (<see cref="ContentBlockResponse"/>, with the body) or the
    /// participant (<see cref="ParticipantContentBlockResponse"/>, body stripped) page as the response body. The
    /// role split is the SAME fail-closed <see cref="ReceivesHostShape"/> classification the unbounded
    /// <see cref="Project"/> uses, so the per-block SHAPE never diverges between the bounded and unbounded list
    /// and an audience role never receives a body (threat T2); only the SET is now bounded. The caller has
    /// already resolved the page bounds and trimmed <paramref name="contentBlocks"/> to at most
    /// <paramref name="limit"/> items and computed <paramref name="hasMore"/>.
    /// </summary>
    public static object ProjectPage(
        IReadOnlyList<ContentBlock> contentBlocks,
        MembershipRole role,
        int offset,
        int limit,
        bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(contentBlocks);

        return ReceivesHostShape(role)
            ? PageResponse.From(contentBlocks.Select(ContentBlockResponse.From).ToArray(), offset, limit, hasMore)
            : PageResponse.From(contentBlocks.Select(ParticipantContentBlockResponse.From).ToArray(), offset, limit, hasMore);
    }

    /// <summary>
    /// Projects a SINGLE content block into the role-appropriate response, boxed as <see cref="object"/> so the
    /// by-id read can return either the full host shape (<see cref="ContentBlockResponse"/>) or the stripped
    /// participant shape (<see cref="ParticipantContentBlockResponse"/>) — the SAME host-vs-participant DTO
    /// split the list uses (<see cref="Project"/>), so the by-id read can never diverge from the list's
    /// projection. An undefined role falls closed to the stripped participant shape.
    /// </summary>
    public static object ProjectOne(ContentBlock contentBlock, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(contentBlock);

        return ReceivesHostShape(role)
            ? ContentBlockResponse.From(contentBlock)
            : ParticipantContentBlockResponse.From(contentBlock);
    }
}
