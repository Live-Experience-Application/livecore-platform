// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// Request body for creating a directed entity relationship edge (CORE-ENT-008,
/// <c>POST /api/v1/workspaces/{workspaceId}/entity-relationships</c>, csv/api_routes.csv "Create directed
/// entity relationship edge", roles Host,CoHost,Owner,Admin).
///
/// The target workspace is taken from the route path and the target organization is supplied as
/// <see cref="OrganizationSlug"/>, mirroring how the entity create resolves its tenant: the slug is matched
/// against the caller's token organization claim AND a persisted organization membership by the tenant
/// context resolver, and the create is then authorized by the caller's role in the route's workspace (threat
/// T5).
///
/// The DTO is generic and product-neutral (docs/04_PRODUCT_BOUNDARIES.md, AGENTS.md): an edge connects a
/// SOURCE <see cref="Entity"/> to a TARGET <see cref="Entity"/> and carries a generic
/// <see cref="RelationshipKind"/> — the DATA-driven label of the edge, never inspected for vocabulary (the
/// template boundary). The edge's surrogate id is assigned SERVER-SIDE (UUIDv7) by the aggregate; the client
/// never supplies an id. BOTH endpoint ids must address entities IN THE SAME WORKSPACE (the create flow
/// resolves each through the workspace-scoped repository — the same-workspace coupling the database foreign
/// key cannot enforce); an endpoint that does not resolve in the caller's workspace is a <c>400</c>, a
/// self-loop (source equals target) is a <c>400</c>, and a duplicate of the same directed edge of the same
/// kind is a <c>409</c>.
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the target workspace, used to resolve the tenant context (the
/// route carries no organization in its path).
/// </param>
/// <param name="SourceEntityId">
/// Surrogate id of the entity the directed edge points FROM. Must address an entity in the route's workspace.
/// </param>
/// <param name="TargetEntityId">
/// Surrogate id of the entity the directed edge points TO. Must address an entity in the route's workspace and
/// be distinct from <see cref="SourceEntityId"/> (no self-loop).
/// </param>
/// <param name="RelationshipKind">
/// Generic, canonical kind/label of the edge (template-/host-supplied data): a lower-case dash-separated slug,
/// stored verbatim and never branched on by Core.
/// </param>
public sealed record CreateEntityRelationshipRequest(
    string? OrganizationSlug,
    string? SourceEntityId,
    string? TargetEntityId,
    string? RelationshipKind);

/// <summary>
/// Response projection of a directed entity relationship edge (CORE-ENT-008), returned by the create route
/// (<c>POST /api/v1/workspaces/{workspaceId}/entity-relationships</c>) and the list read
/// (<c>GET /api/v1/workspaces/{workspaceId}/entity-relationships</c>).
///
/// An edge is a structural/authoring graph artifact, NOT audience content (it carries no free-form content —
/// only endpoint identifiers and a canonical kind slug), so — like the entity-type reads — the relationship
/// reads are restricted to the authoring roles (Owner/Admin/Host/CoHost) and there is a SINGLE projection (no
/// host-vs-participant split). The DTO carries identifiers, the tenant/workspace boundaries, the directed
/// endpoints, the generic kind and the server timestamps; it carries NO authorization rationale and never
/// echoes how the tenant/workspace was resolved (docs/08; threat T7). The <see cref="RelationshipKind"/> is a
/// canonical slug identifier (DATA), never free-form content, so it is safe to project (the same reasoning
/// that keeps it loggable on the aggregate).
/// </summary>
/// <param name="Id">Surrogate id of the relationship (UUIDv7).</param>
/// <param name="OrganizationId">Tenant the relationship belongs to.</param>
/// <param name="WorkspaceId">Workspace the relationship belongs to.</param>
/// <param name="SourceEntityId">The entity the directed edge points FROM.</param>
/// <param name="TargetEntityId">The entity the directed edge points TO.</param>
/// <param name="RelationshipKind">The generic, canonical kind/label of the edge.</param>
/// <param name="CreatedAt">When the relationship was created (UTC).</param>
/// <param name="UpdatedAt">When the relationship was last updated (UTC).</param>
public sealed record EntityRelationshipResponse(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid SourceEntityId,
    Guid TargetEntityId,
    string RelationshipKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects an <see cref="EntityRelationship"/> aggregate into its response DTO. Only the generic,
    /// server-side fields are copied.
    /// </summary>
    public static EntityRelationshipResponse From(EntityRelationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        return new EntityRelationshipResponse(
            relationship.Id,
            relationship.OrganizationId,
            relationship.WorkspaceId,
            relationship.SourceEntityId,
            relationship.TargetEntityId,
            relationship.RelationshipKind,
            relationship.CreatedAt,
            relationship.UpdatedAt);
    }
}
