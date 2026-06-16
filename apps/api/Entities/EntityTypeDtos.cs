namespace LiveCore.Api.Entities;

/// <summary>
/// Request body for defining an entity type (CORE-ENT-007,
/// <c>POST /api/v1/workspaces/{workspaceId}/entity-types</c>, csv/api_routes.csv "Create entity type", roles
/// Host,CoHost,Owner,Admin).
///
/// The target workspace is taken from the route path and the target organization is supplied as
/// <see cref="OrganizationSlug"/>, mirroring how the entity create resolves its tenant: the slug is matched
/// against the caller's token organization claim AND a persisted organization membership by the tenant context
/// resolver, and the create is then authorized by the caller's role in the route's workspace (threat T5).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md, AGENTS.md): an entity type is the
/// DATA-DRIVEN definition of a kind of entity — a stable natural <see cref="TypeKey"/>, a human
/// <see cref="DisplayName"/> and the template-defined attribute <see cref="AttributeSchema"/> — all
/// template-/host-supplied DATA, never inspected for vocabulary and never branched on (the template boundary;
/// no <c>if entityType == ...</c> in Core source). The type's surrogate id is assigned SERVER-SIDE (UUIDv7) by
/// the aggregate; the client never supplies an id. The <see cref="TypeKey"/> is unique WITHIN the route's
/// workspace; a key the workspace already defines is a <c>409 Conflict</c>.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the target workspace, used to resolve the tenant context (the
/// route carries no organization in its path).
/// </param>
/// <param name="TypeKey">
/// The type's stable, canonical natural key, unique within the route's workspace (canonicalized to a lower-case
/// dash-separated slug; a near-match or re-cased value canonicalizes to the same key).
/// </param>
/// <param name="DisplayName">Human-readable label of the type (template-/host-supplied data).</param>
/// <param name="AttributeSchema">
/// The type's template-defined attribute schema as a JSON document (template-/host-supplied data). Optional:
/// when omitted or blank the type is created with an empty JSON object (<c>{}</c>); when provided it must be
/// well-formed JSON within the size bound.
/// </param>
public sealed record CreateEntityTypeRequest(
    string? OrganizationSlug,
    string? TypeKey,
    string? DisplayName,
    string? AttributeSchema);

/// <summary>
/// Response projection of an entity type (CORE-ENT-007). It is the FULL, product-neutral, server-side view of a
/// generic entity-type DEFINITION returned to the authoring roles (Owner/Admin/Host/CoHost) on the create, list
/// and by-id read routes.
///
/// Unlike the entity DTOs (CORE-ENT-006), there is exactly ONE entity-type shape: an entity type is an
/// authoring/schema artifact (a vertical's domain-to-Core mapping, the template boundary), not audience content,
/// so all three routes are authorized to the authoring roles only and there is no host-vs-participant split —
/// no audience role ever receives an entity type. The shape is generic: the type's <see cref="TypeKey"/>,
/// <see cref="DisplayName"/> and <see cref="AttributeSchema"/> are stored DATA, never inspected for vocabulary.
///
/// The DTO carries identifiers, the tenant/workspace boundaries, the natural key, the display name, the
/// attribute schema and the server timestamps. It carries NO internal authorization rationale — it never echoes
/// why the caller was allowed or how the tenant/workspace was resolved (docs/08; threat T7). Server timestamps
/// are included per docs/08; there is no resource version/ETag field (these create/list/read operations have no
/// concurrent update to guard at this surface).
/// </summary>
/// <param name="Id">Surrogate id of the entity type (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the entity type belongs to.</param>
/// <param name="WorkspaceId">Workspace the entity type belongs to.</param>
/// <param name="TypeKey">The type's canonical natural key (unique within the workspace).</param>
/// <param name="DisplayName">Human-readable label of the entity type.</param>
/// <param name="AttributeSchema">The type's template-defined attribute schema (a JSON document).</param>
/// <param name="CreatedAt">When the entity type was created (UTC).</param>
/// <param name="UpdatedAt">When the entity type was last updated (UTC).</param>
public sealed record EntityTypeResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    string TypeKey,
    string DisplayName,
    string AttributeSchema,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects an <see cref="EntityType"/> aggregate into its response DTO. Only the generic, server-side
    /// fields are copied.
    /// </summary>
    public static EntityTypeResponse From(EntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        return new EntityTypeResponse(
            entityType.Id,
            entityType.OrganizationId,
            entityType.WorkspaceId,
            entityType.TypeKey,
            entityType.DisplayName,
            entityType.AttributeSchema,
            entityType.CreatedAt,
            entityType.UpdatedAt);
    }
}
