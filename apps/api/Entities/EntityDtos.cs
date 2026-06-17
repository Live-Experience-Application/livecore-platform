// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Entities;

/// <summary>
/// Request body for creating an entity (CORE-ENT-006,
/// <c>POST /api/v1/workspaces/{workspaceId}/entities</c>, csv/api_routes.csv "Create entity", roles
/// Host,CoHost,Owner,Admin).
///
/// The target workspace is taken from the route path and the target organization is supplied as
/// <see cref="OrganizationSlug"/>, mirroring how the scene create resolves its tenant: the slug is matched
/// against the caller's token organization claim AND a persisted organization membership by the tenant
/// context resolver, and the create is then authorized by the caller's role in the route's workspace (threat
/// T5).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md, AGENTS.md): an entity is an
/// instance of an entity TYPE (<see cref="EntityTypeId"/>) and carries a human <see cref="Name"/> and its
/// actual attribute <see cref="AttributeValues"/> — all template-/host-supplied DATA, never inspected for
/// vocabulary. The entity's surrogate id is assigned SERVER-SIDE (UUIDv7) by the aggregate; the client never
/// supplies an id. The referenced <see cref="EntityTypeId"/> must address an entity type IN THE SAME
/// WORKSPACE (the create flow resolves it through the workspace-scoped repository — the same-workspace
/// coupling the database foreign key cannot enforce); a type id that does not resolve in the caller's
/// workspace is a <c>400</c>.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the target workspace, used to resolve the tenant context (the
/// route carries no organization in its path).
/// </param>
/// <param name="EntityTypeId">
/// Surrogate id of the entity type the new entity is an instance of. Must address a type in the route's
/// workspace.
/// </param>
/// <param name="Name">Human-readable label of the new entity (template-/host-supplied data).</param>
/// <param name="AttributeValues">
/// The entity's attribute values as a JSON document (template-/host-supplied data). Optional: when omitted or
/// blank the entity is created with an empty JSON object (<c>{}</c>); when provided it must be well-formed
/// JSON within the size bound.
/// </param>
public sealed record CreateEntityRequest(
    string? OrganizationSlug,
    string? EntityTypeId,
    string? Name,
    string? AttributeValues);

/// <summary>
/// HOST-facing response projection of an entity (CORE-ENT-006). It is the FULL, product-neutral, server-side
/// view of a generic entity returned to the host-CONTENT workspace roles (Owner/Admin/Host/CoHost) on the
/// create, list and by-id read routes.
///
/// This is one of the TWO distinct entity DTO types CORE-ENT-006 requires (docs/08_API_CONTRACTS.md DTO
/// design rule "Host DTOs and Participant DTOs are different"): it is the full host projection, while
/// <see cref="ParticipantEntityResponse"/> is the host-only-field-stripped, audience-safe projection.
/// <see cref="EntityProjection"/> selects between them from the caller's workspace role; this type is NEVER
/// returned to an audience (Participant/Observer) or audit (Auditor) caller.
///
/// An entity IS content (its <see cref="AttributeValues"/> is host-prepared data), so — unlike the scene
/// METADATA projection — the role split here is the "View host-only content" row of
/// docs/06_AUTHORIZATION_MATRIX.md, not "View workspace metadata": only the host-content roles receive this
/// shape (see <see cref="EntityProjection"/>).
///
/// The DTO carries identifiers, the tenant/workspace boundaries, the type linkage, the human name, the
/// attribute values and the server timestamps. It carries NO visibility fields and NO internal authorization
/// rationale — it never echoes why the caller was allowed or how the tenant/workspace was resolved (docs/08;
/// threat T7). Server timestamps are included per docs/08; there is no resource version/ETag field (these
/// create/list/read operations have no concurrent update to guard at this surface).
/// </summary>
/// <param name="Id">Surrogate id of the entity (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the entity belongs to.</param>
/// <param name="WorkspaceId">Workspace the entity belongs to.</param>
/// <param name="EntityTypeId">The entity type this entity is an instance of.</param>
/// <param name="Name">Human-readable label of the entity.</param>
/// <param name="AttributeValues">The entity's attribute values (a JSON document).</param>
/// <param name="CreatedAt">When the entity was created (UTC).</param>
/// <param name="UpdatedAt">When the entity was last updated (UTC).</param>
public sealed record EntityResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid EntityTypeId,
    string Name,
    string AttributeValues,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects an <see cref="Entity"/> aggregate into its full host response DTO. Only the generic,
    /// server-side fields are copied.
    /// </summary>
    public static EntityResponse From(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new EntityResponse(
            entity.Id,
            entity.OrganizationId,
            entity.WorkspaceId,
            entity.EntityTypeId,
            entity.Name,
            entity.AttributeValues,
            entity.CreatedAt,
            entity.UpdatedAt);
    }
}

/// <summary>
/// PARTICIPANT-facing response projection of an entity (CORE-ENT-006). It is the audience-safe,
/// host-only-field-STRIPPED half of the host-vs-participant DTO separation for the entity list and by-id read
/// routes: the shape returned to the audience workspace roles (Participant/Observer) — and, fail-closed, the
/// audit role (Auditor) and any undefined role — instead of the full <see cref="EntityResponse"/>.
///
/// docs/08_API_CONTRACTS.md states "Host DTOs and Participant DTOs are different" and "Participant DTOs must
/// not contain hidden content fields", so it is a separate record type, NOT a reused/optional-field variant
/// of the host DTO. An entity IS content (docs/06 "View host-only content"), so the audience-safe shape
/// carries only the minimal, non-sensitive entity IDENTITY — the surrogate handle and the human label — and
/// strips everything host-only.
///
/// EXACT field set and the doc citation justifying each OMISSION:
/// <list type="bullet">
///   <item><c>Id</c> — KEPT: the surrogate id is a non-sensitive correlation handle; it never authorizes
///   access on its own (every lookup is tenant+workspace scoped, threats T1/T5).</item>
///   <item><c>Name</c> — KEPT: the entity's human-readable label, the minimal audience-relevant identity of
///   the entity (the analogue of the scene title kept in <see cref="Scenes.ParticipantSceneResponse"/>).</item>
///   <item><c>AttributeValues</c> — OMITTED: the entity's actual attribute DATA is host-only CONTENT
///   (docs/06 "View host-only content" = no for the audience roles, audit-only for Auditor; docs/08 forbids
///   hidden content fields in participant DTOs). What an audience member may actually see of an entity is
///   computed server-side by the Visibility engine in the entity-search read (CORE-ENT-005), never exposed
///   wholesale here (threats T2/T7).</item>
///   <item><c>OrganizationId</c> / <c>WorkspaceId</c> / <c>EntityTypeId</c> — OMITTED: internal tenant,
///   workspace and type-linkage identifiers, stripped exactly as <see cref="Scenes.ParticipantSceneResponse"/>
///   strips the tenant/workspace boundary ids (docs/06 "limited"; docs/08 no hidden/internal fields).</item>
///   <item><c>CreatedAt</c> / <c>UpdatedAt</c> — OMITTED: host preparation timestamps, host-only metadata an
///   audience member does not receive (docs/08; the host DTO satisfies "Include server timestamps").</item>
/// </list>
///
/// It carries NO hidden content fields, NO host-only fields and NO internal authorization rationale (docs/08;
/// threats T2/T7). Adding any host-only field to this record would be caught by the integration tests that
/// assert its EXACT top-level JSON property set.
/// </summary>
/// <param name="Id">Surrogate id of the entity (UUIDv7); a non-sensitive handle.</param>
/// <param name="Name">Human-readable label of the entity.</param>
public sealed record ParticipantEntityResponse(
    Guid Id,
    string Name)
{
    /// <summary>
    /// Projects an <see cref="Entity"/> aggregate into its audience-safe participant DTO. Only the minimal
    /// audience-relevant fields (id, name) are copied; the boundary/type ids, the attribute-values content
    /// and the host preparation timestamps are deliberately NOT copied (see the type summary).
    /// </summary>
    public static ParticipantEntityResponse From(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new ParticipantEntityResponse(entity.Id, entity.Name);
    }
}

/// <summary>
/// Role-based entity PROJECTOR (CORE-ENT-006) — the pure mapping that honors the "Projection by role"
/// obligation on the entity list and by-id read routes (csv/api_routes.csv). It selects WHICH of the two
/// distinct entity DTO shapes (<see cref="EntityResponse"/> host shape vs
/// <see cref="ParticipantEntityResponse"/> audience shape) a caller receives, from the caller's WORKSPACE
/// <see cref="MembershipRole"/>.
///
/// It changes only the SHAPE of each entity, never the SET of entities: every member of a workspace receives
/// ALL of that workspace's entities (the list is allowed to "workspace members" per csv/api_routes.csv).
/// Deciding WHICH entities a participant may actually see (audience calculation, per-entity "if visible"
/// filtering) is the central Visibility module's job (docs/05_MODULE_CONTRACTS.md), realized by the
/// entity-search read (CORE-ENT-005, <see cref="EntitySearchService"/>) — NOT this projector. This type
/// contains NO visibility-engine logic.
///
/// Role -> shape mapping. An entity IS content, so the split is decided STRICTLY from the "View host-only
/// content" row of docs/06_AUTHORIZATION_MATRIX.md (NOT "View workspace metadata"). It DELEGATES to the
/// central Visibility module's canonical classification (<see cref="VisibilityRoles.ViewsHostOnlyContent"/>,
/// CORE-VIS-002) so the host-content role set (Owner/Admin/Host/CoHost) is defined in ONE place and never
/// duplicated (docs/02_ARCHITECTURE.md; docs/05_MODULE_CONTRACTS.md) — the same delegation
/// <see cref="EntitySearchVisibility"/> uses.
/// <list type="bullet">
///   <item>Owner, Admin, Host, CoHost -> <see cref="EntityResponse"/> (host-content roles; "View host-only
///   content" = yes).</item>
///   <item>Participant, Observer (audience), Auditor (audit-only) and any undefined value ->
///   <see cref="ParticipantEntityResponse"/>. The audience roles get the stripped shape because their content
///   access is "if visible" (computed elsewhere); Auditor is EXCLUDED from the host-content shape because the
///   matrix grants it only <c>audit-only</c>, not <c>yes</c> ("Audit roles … should not automatically view
///   sensitive content unless explicitly allowed", docs/06) — the deliberate distinction from
///   <see cref="Scenes.SceneProjection"/>, which gives Auditor the host SHAPE because that projector exposes
///   only scene METADATA whereas an entity IS content.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is NON-LINEAR (the enum's integer values are storage discriminators only and
/// must never be compared with &gt;/&lt;), so the classification is an EXACT set-membership check, never an
/// ordering comparison. Any value outside the host-content set fails closed to the stripped participant shape
/// (deny-by-default: an unrecognized role is never granted the host-content view; threats T1/T5).
/// </summary>
internal static class EntityProjection
{
    /// <summary>
    /// Whether the given workspace role receives the full HOST entity shape (<see cref="EntityResponse"/>,
    /// including the attribute-values content) rather than the stripped
    /// <see cref="ParticipantEntityResponse"/>. DELEGATES to the central
    /// <see cref="VisibilityRoles.ViewsHostOnlyContent"/> (Owner/Admin/Host/CoHost); every other role
    /// (Participant/Observer, Auditor and any undefined value) is denied the host shape and falls closed to
    /// the participant shape. Never a &gt;/&lt; comparison.
    /// </summary>
    public static bool ReceivesHostShape(MembershipRole role)
        => VisibilityRoles.ViewsHostOnlyContent(role);

    /// <summary>
    /// Projects the given entities into the role-appropriate response array, boxed as <see cref="object"/> so
    /// the endpoint can return either the host (<see cref="EntityResponse"/>) or the participant
    /// (<see cref="ParticipantEntityResponse"/>) array as the response body. The SET of entities is unchanged
    /// — only the per-entity SHAPE differs by role.
    /// </summary>
    public static object Project(IEnumerable<Entity> entities, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return ReceivesHostShape(role)
            ? entities.Select(EntityResponse.From).ToArray()
            : entities.Select(ParticipantEntityResponse.From).ToArray();
    }

    /// <summary>
    /// Projects ONE BOUNDED PAGE of entities into the role-appropriate
    /// <see cref="LiveCore.Api.PageResponse{T}"/> envelope (CORE-DX-003), boxed as <see cref="object"/> so the
    /// endpoint can return either the host (<see cref="EntityResponse"/>) or the participant
    /// (<see cref="ParticipantEntityResponse"/>) page as the response body. The role split is the SAME
    /// fail-closed <see cref="ReceivesHostShape"/> classification the unbounded <see cref="Project"/> uses, so
    /// the per-entity SHAPE never diverges between the bounded and unbounded list; only the SET is now bounded.
    /// The caller has already resolved the page bounds and trimmed <paramref name="entities"/> to at most
    /// <paramref name="limit"/> items and computed <paramref name="hasMore"/>.
    /// </summary>
    public static object ProjectPage(
        IReadOnlyList<Entity> entities,
        MembershipRole role,
        int offset,
        int limit,
        bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return ReceivesHostShape(role)
            ? PageResponse.From(entities.Select(EntityResponse.From).ToArray(), offset, limit, hasMore)
            : PageResponse.From(entities.Select(ParticipantEntityResponse.From).ToArray(), offset, limit, hasMore);
    }

    /// <summary>
    /// Projects a SINGLE entity into the role-appropriate response, boxed as <see cref="object"/> so the
    /// by-id read can return either the full host shape (<see cref="EntityResponse"/>) or the stripped
    /// participant shape (<see cref="ParticipantEntityResponse"/>) — the SAME host-vs-participant DTO split
    /// the list uses (<see cref="Project"/>), so the by-id read can never diverge from the list's projection.
    /// An undefined role falls closed to the stripped participant shape.
    /// </summary>
    public static object ProjectOne(Entity entity, MembershipRole role)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return ReceivesHostShape(role)
            ? EntityResponse.From(entity)
            : ParticipantEntityResponse.From(entity);
    }
}
