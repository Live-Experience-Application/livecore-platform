using LiveCore.Api.Organizations;

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
/// HOST-facing response projection of a scene (CORE-SCENE-003 introduced it;
/// CORE-SCENE-004 makes it the HOST half of the host-vs-participant DTO separation
/// for <c>GET /api/v1/workspaces/{workspaceId}/scenes</c> and the create response).
/// It is the FULL, product-neutral, server-side view of a prepared scene returned to
/// the host-capable / metadata-entitled workspace roles.
///
/// This is one of the TWO distinct scene DTO types CORE-SCENE-004 requires
/// (docs/08_API_CONTRACTS.md DTO design rule "Host DTOs and Participant DTOs are
/// different"): it is the full host projection, while
/// <see cref="ParticipantSceneResponse"/> is the host-only-field-stripped,
/// audience-safe projection. <see cref="SceneProjection"/> selects between them from
/// the caller's workspace role; this type is NEVER returned to an audience
/// (Participant/Observer) caller. Which caller gets which shape is driven by the
/// "View workspace metadata" row of docs/06_AUTHORIZATION_MATRIX.md (see
/// <see cref="SceneProjection"/>).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md,
/// docs/08_API_CONTRACTS.md DTO design rules): identifiers, the tenant and workspace
/// boundaries, the display title, the ordering position and the server timestamps —
/// the full set of host-prepared scene metadata. It carries NO visibility fields and
/// NO internal authorization rationale — it never echoes why the caller was allowed
/// or how the tenant/workspace was resolved (docs/08; threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
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

/// <summary>
/// PARTICIPANT-facing response projection of a scene (CORE-SCENE-004). It is the
/// audience-safe, host-only-field-STRIPPED half of the host-vs-participant DTO
/// separation for <c>GET /api/v1/workspaces/{workspaceId}/scenes</c>: the shape
/// returned to the audience workspace roles (Participant/Observer) instead of the
/// full <see cref="SceneResponse"/>.
///
/// This is the second of the TWO distinct scene DTO types CORE-SCENE-004 requires;
/// docs/08_API_CONTRACTS.md states "Host DTOs and Participant DTOs are different" and
/// "Participant DTOs must not contain hidden content fields", so it is a separate
/// record type, NOT a reused/optional-field variant of the host DTO. Per docs/06
/// the audience roles have only "limited" "View workspace metadata", so the
/// participant shape carries only the minimal audience-relevant scene identity and
/// its sequence position.
///
/// EXACT field set and the doc citation justifying each OMISSION (a deliberately
/// minimal, audience-safe set):
/// <list type="bullet">
///   <item><c>Id</c> — KEPT: the surrogate id is a non-sensitive correlation handle a
///   client needs to address the scene; it never authorizes access on its own (every
///   lookup is tenant+workspace scoped, threat T1/T5).</item>
///   <item><c>Title</c> — KEPT: the scene's human-readable display label, the minimal
///   audience-relevant identity of a scene segment (docs/03_DOMAIN_LANGUAGE.md: a
///   scene is a "Segment of a session").</item>
///   <item><c>Order</c> — KEPT: the audience-relevant sequence position so a
///   participant client can render the scenes in order; it is an ordering position,
///   not host-only preparation state.</item>
///   <item><c>OrganizationId</c> — OMITTED: the internal TENANT boundary identifier.
///   docs/06 grants the audience roles only "limited" "View workspace metadata", and
///   docs/08 forbids hidden/internal fields in participant DTOs; the tenant id is an
///   internal isolation boundary (threat T5), not audience-relevant scene identity, so
///   it is stripped.</item>
///   <item><c>WorkspaceId</c> — OMITTED: the internal WORKSPACE boundary identifier,
///   stripped for the same reason as the tenant id (docs/06 "limited"; docs/08 no
///   hidden/internal fields). The audience addresses scenes through the workspace it is
///   already in, so echoing the boundary id back adds no audience value and is
///   internal-boundary metadata.</item>
///   <item><c>CreatedAt</c> / <c>UpdatedAt</c> — OMITTED: host preparation
///   timestamps. They reveal when a host prepared/edited a scene (host preparation
///   activity), which is host-only metadata an audience member has only "limited"
///   access to (docs/06) and which docs/08 keeps out of participant DTOs. (Note: docs/08
///   "Include server timestamps" is satisfied by the host DTO; the participant shape
///   omits the host-only preparation timestamps deliberately.)</item>
/// </list>
///
/// It carries NO hidden content fields (there is no content-block field here at all),
/// NO host-only fields and NO internal authorization rationale — it never echoes why
/// the caller received the participant shape or how the tenant/workspace was resolved
/// (docs/08 "Never include internal authorization rationale in participant responses";
/// threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md). Adding any host-only field to
/// this record would be caught by the integration tests that assert its EXACT
/// top-level JSON property set.
/// </summary>
/// <param name="Id">Surrogate id of the scene (UUIDv7); a non-sensitive handle.</param>
/// <param name="Title">Human-readable display title of the scene.</param>
/// <param name="Order">Ordering position of the scene within its workspace.</param>
public sealed record ParticipantSceneResponse(
    Guid Id,
    string Title,
    int Order)
{
    /// <summary>
    /// Projects a <see cref="Scene"/> aggregate into its audience-safe participant
    /// DTO. Only the minimal audience-relevant fields (id, title, order) are copied;
    /// the tenant/workspace boundary ids and the host preparation timestamps are
    /// deliberately NOT copied (see the type summary for the per-field doc citation).
    /// </summary>
    public static ParticipantSceneResponse From(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        return new ParticipantSceneResponse(
            scene.Id,
            scene.Title,
            scene.Order);
    }
}

/// <summary>
/// Role-based scene-list PROJECTOR (CORE-SCENE-004) — the pure mapping that honors the
/// "Projection by role" obligation on <c>GET /api/v1/workspaces/{workspaceId}/scenes</c>
/// (csv/api_routes.csv line 15). It selects WHICH of the two distinct scene DTO shapes
/// (<see cref="SceneResponse"/> host shape vs <see cref="ParticipantSceneResponse"/>
/// audience shape) a caller receives, from the caller's WORKSPACE
/// <see cref="MembershipRole"/>.
///
/// It changes only the SHAPE of each scene, never the SET of scenes: every member of a
/// workspace still receives ALL of that workspace's scenes (the list is allowed to "any
/// member" per csv/api_routes.csv). Deciding WHICH scenes/blocks a participant may see
/// (audience calculation, "if visible" filtering, preview-as-participant,
/// <c>CanViewResource</c>) is the Visibility module's job (docs/05_MODULE_CONTRACTS.md:
/// the Visibility module owns "visibility rules", "audience calculations" and
/// "preview-as-participant"; docs/06 authorization principle "Participant visibility is
/// computed server-side") and is the later CORE-VIS-* epic — NOT this projector. This
/// type contains NO visibility-engine logic.
///
/// Role -> shape mapping, decided STRICTLY from the "View workspace metadata" row of
/// docs/06_AUTHORIZATION_MATRIX.md (csv/authorization_matrix.csv): Owner/Admin/Host/
/// CoHost = <c>yes</c> and Auditor = <c>yes</c> get the full HOST/metadata shape;
/// Participant/Observer = <c>limited</c> get the stripped PARTICIPANT shape.
/// <list type="bullet">
///   <item>Owner, Admin, Host, CoHost -> <see cref="SceneResponse"/> (host-capable
///   roles; "View workspace metadata" = yes).</item>
///   <item>Auditor -> <see cref="SceneResponse"/>. The matrix grants Auditor "View
///   workspace metadata" = <c>yes</c> (the same as the host roles for METADATA), so the
///   auditor receives the full metadata shape. This is metadata only: it is NOT the
///   "View host-only content" row (where Auditor is merely <c>audit-only</c>), and this
///   projector projects only scene METADATA, never content blocks — so giving Auditor
///   the metadata shape does not grant it any host-only CONTENT.</item>
///   <item>Participant, Observer -> <see cref="ParticipantSceneResponse"/> (audience
///   roles; "View workspace metadata" = <c>limited</c>, so the stripped shape).</item>
/// </list>
///
/// <see cref="MembershipRole"/> is NON-LINEAR (the authorization matrix is non-linear;
/// the enum's integer values are storage discriminators only and must never be compared
/// with &gt;/&lt;), so the classification is an EXACT set-membership check over the
/// host/metadata roles, never an ordering comparison. An undefined enum value fails
/// closed to the stripped participant shape (deny-by-default: an unrecognized role is
/// never granted the broader host metadata view).
/// </summary>
internal static class SceneProjection
{
    /// <summary>
    /// Whether the given workspace role receives the full HOST/metadata scene shape
    /// (<see cref="SceneResponse"/>) rather than the stripped
    /// <see cref="ParticipantSceneResponse"/>. EXACT set membership over the roles whose
    /// "View workspace metadata" is <c>yes</c> in docs/06: Owner, Admin, Host, CoHost
    /// and Auditor. Any other role (the audience roles Participant/Observer, whose
    /// metadata access is <c>limited</c>, and any undefined value) is denied the host
    /// shape and falls closed to the participant shape. Never a &gt;/&lt; comparison.
    /// </summary>
    public static bool ReceivesHostShape(MembershipRole role)
        => role is MembershipRole.Owner
            or MembershipRole.Admin
            or MembershipRole.Host
            or MembershipRole.CoHost
            or MembershipRole.Auditor;

    /// <summary>
    /// Projects the given scenes into the role-appropriate response array, boxed as
    /// <see cref="object"/> so the endpoint can return either the host
    /// (<see cref="SceneResponse"/>) or the participant
    /// (<see cref="ParticipantSceneResponse"/>) array as the response body. The SET of
    /// scenes is unchanged — only the per-scene SHAPE differs by role.
    /// </summary>
    public static object Project(IEnumerable<Scene> scenes, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(scenes);

        return ReceivesHostShape(role)
            ? scenes.Select(SceneResponse.From).ToArray()
            : scenes.Select(ParticipantSceneResponse.From).ToArray();
    }
}
